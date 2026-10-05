using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public sealed class ProxyTopologyEntry
{
    public string Type { get; set; }
    public string Selected { get; set; }
    public string[] Choices { get; set; }
}

public interface IProxyTopologyClient
{
    IDictionary<string, ProxyTopologyEntry> ReadProxyTopology();
    string GetRoutingMode();
}

// Binds recovery to the user's existing selector path, never to a synthetic traffic group.
internal sealed class MonitorSelectionBinding
{
    public string Root { get; private set; }
    public string Group { get; private set; }
    public string Node { get; private set; }
    public string Key { get; private set; }
    public string ExpectedSelection { get; set; }
    private readonly Dictionary<string, string> parents = new Dictionary<string, string>(StringComparer.Ordinal);

    public static MonitorSelectionBinding Resolve(IDictionary<string, ProxyTopologyEntry> topology,
        string preferred, string mode)
    {
        if (String.Equals(mode, "direct", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Clash 当前为直连模式，暂停代理节点恢复");
        string root = String.Equals(mode, "global", StringComparison.OrdinalIgnoreCase) && topology.ContainsKey("GLOBAL")
            ? "GLOBAL" : new[] { preferred, "🚀 节点选择", "PROXY", "Proxy", "代理" }
                .FirstOrDefault(x => !String.IsNullOrEmpty(x) && topology.ContainsKey(x) && IsSelector(topology[x]));
        if (root == null)
            root = topology.Where(x => IsSelector(x.Value) && x.Key != "GLOBAL" && x.Key != "🧪 兼容性探测" && x.Key != "🌐 统一稳定节点")
                .OrderByDescending(x => topology.Where(y => y.Key != "GLOBAL").Count(y =>
                    (y.Value.Choices ?? new string[0]).Contains(x.Key)))
                .ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).FirstOrDefault();
        if (root == null) throw new InvalidOperationException("没有找到可监控的现有手动代理组");
        var binding = new MonitorSelectionBinding { Root = root, Group = root };
        string name = root;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(name)) throw new InvalidDataException("代理组选择存在循环");
            ProxyTopologyEntry entry;
            if (!topology.TryGetValue(name, out entry)) throw new InvalidDataException("代理组选择不存在");
            if (entry.Choices == null || entry.Choices.Length == 0) break;
            if (String.IsNullOrEmpty(entry.Selected)) throw new InvalidDataException("代理组尚未选择节点");
            if (IsSelector(entry)) binding.Group = name;
            binding.parents[name] = entry.Selected;
            name = entry.Selected;
        }
        binding.Node = name;
        binding.ExpectedSelection = topology[binding.Group].Selected;
        binding.parents.Remove(binding.Group);
        // Descendants below a writable selector can be automatic groups; their current leaf is not an ownership guard.
        string cursor = root;
        var path = new List<string>();
        while (cursor != binding.Group)
        {
            string next = binding.parents[cursor];
            path.Add(cursor + "=" + next);
            cursor = next;
        }
        binding.parents.Clear();
        cursor = root;
        while (cursor != binding.Group)
        {
            binding.parents[cursor] = topology[cursor].Selected;
            cursor = topology[cursor].Selected;
        }
        binding.Key = root + "\n" + String.Join("\n", path) + "\n" + binding.Group;
        return binding;
    }

    public bool StillOwned(IDictionary<string, ProxyTopologyEntry> topology)
    {
        return topology.ContainsKey(Group) && IsSelector(topology[Group]) &&
            topology[Group].Selected == ExpectedSelection && parents.All(pair =>
            topology.ContainsKey(pair.Key) && topology[pair.Key].Selected == pair.Value);
    }

    private static bool IsSelector(ProxyTopologyEntry entry)
    { return entry != null && String.Equals(entry.Type, "Selector", StringComparison.OrdinalIgnoreCase); }
}

public sealed partial class MonitorWorker
{
    private string SelectionKey { get { return selectionBinding == null ? config.SharedGroup : selectionBinding.Key; } }

    private void BindSelection()
    {
        var topology = mihomo as IProxyTopologyClient;
        if (topology == null) return;
        selectionMode = topology.GetRoutingMode();
        selectionBinding = MonitorSelectionBinding.Resolve(topology.ReadProxyTopology(), preferredMonitorGroup, selectionMode);
        config.SharedGroup = selectionBinding.Group;
    }

    private string ReadCurrentSelection()
    {
        var topology = mihomo as IProxyTopologyClient;
        return topology == null || selectionBinding == null ? mihomo.GetSelected(config.SharedGroup) :
            MonitorSelectionBinding.Resolve(topology.ReadProxyTopology(), selectionBinding.Root, topology.GetRoutingMode()).Node;
    }

    private bool SelectionStillOwned()
    {
        var topology = mihomo as IProxyTopologyClient;
        return topology == null || (selectionBinding != null && topology.GetRoutingMode() == selectionMode &&
            selectionBinding.StillOwned(topology.ReadProxyTopology()));
    }

    private void WriteSelectedNode(string expected, string target)
    {
        if (!SelectionStillOwned() || ReadCurrentSelection() != expected)
            throw new InvalidOperationException("代理组或用户选择已经变化，取消节点写入");
        if (!mihomo.GetChoices(config.SharedGroup).Contains(target))
            throw new InvalidOperationException("候选不是当前代理组的可选节点");
        if (!SelectionStillOwned() || ReadCurrentSelection() != expected)
            throw new InvalidOperationException("写入前代理组或用户选择已经变化");
        try { mihomo.Select(config.SharedGroup, target); }
        finally
        {
            if (selectionBinding != null && mihomo.GetSelected(config.SharedGroup) == target)
                selectionBinding.ExpectedSelection = target;
        }
    }
}
