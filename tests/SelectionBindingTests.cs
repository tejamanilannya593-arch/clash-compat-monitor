using System;
using System.Collections.Generic;
using System.IO;

internal static partial class Tests
{
    private sealed class ExistingSelectorClient : IMihomoClient, IRecoveryConnectivityClient, IProxyTopologyClient
    {
        public readonly RecoveryClient Inner = new RecoveryClient();
        public string[] GetChoices(string group) { return Inner.GetChoices(group == "PROXY" ? "shared" : group); }
        public string GetSelected(string group) { return Inner.GetSelected(group == "PROXY" ? "shared" : group); }
        public void Select(string group, string node) { Inner.Select(group == "PROXY" ? "shared" : group, node); }
        public int GetDelay(string node, string url, int timeout) { return Inner.GetDelay(node, url, timeout); }
        public bool IsRuntimeIpv6Enabled() { return false; }
        public bool IsAvailable() { return true; }
        public string GetRoutingMode() { return "rule"; }
        public RecoveryEvidence CheckConnectivity(string node, string url, int timeout, out int milliseconds)
        { return Inner.CheckConnectivity(node, url, timeout, out milliseconds); }
        public IDictionary<string, ProxyTopologyEntry> ReadProxyTopology()
        {
            var result = new Dictionary<string, ProxyTopologyEntry>();
            result["PROXY"] = new ProxyTopologyEntry { Type = "Selector", Selected = Inner.Current, Choices = GetChoices("PROXY") };
            foreach (string node in GetChoices("PROXY")) result[node] = new ProxyTopologyEntry { Type = "Shadowsocks" };
            return result;
        }
    }

    private static void ExistingSelectorBehavior()
    {
        var topology = new Dictionary<string, ProxyTopologyEntry> {
            { "PROXY", new ProxyTopologyEntry { Type = "Selector", Selected = "region", Choices = new[] { "region", "b" } } },
            { "region", new ProxyTopologyEntry { Type = "Selector", Selected = "a", Choices = new[] { "a", "b" } } },
            { "a", new ProxyTopologyEntry { Type = "Shadowsocks" } },
            { "b", new ProxyTopologyEntry { Type = "VLESS" } }
        };
        MonitorSelectionBinding binding = MonitorSelectionBinding.Resolve(topology, "🚀 节点选择", "rule");
        Equal("PROXY", binding.Root, "existing PROXY group works without any dedicated traffic group");
        Equal("region", binding.Group, "nested manual selector controls the actual selected leaf");
        Equal("a", binding.Node, "nested selection resolves the actual node");
        topology["region"].Selected = "b";
        Equal(false, binding.StillOwned(topology), "direct manual choice invalidates selector ownership");
        topology["region"].Selected = "a";
        topology["PROXY"].Selected = "b";
        Equal(false, binding.StillOwned(topology), "parent manual change invalidates recovery ownership");
        topology["PROXY"].Selected = "region";
        topology["region"].Selected = "PROXY";
        Throws<InvalidDataException>(() => MonitorSelectionBinding.Resolve(topology, "PROXY", "rule"), "cyclic group graph is rejected");
        topology["region"].Selected = "a";
        topology["PROXY"].Selected = "a";
        binding = MonitorSelectionBinding.Resolve(topology, "PROXY", "rule");
        topology["PROXY"].Selected = "region";
        Equal(false, binding.StillOwned(topology), "new nested selection with the same leaf cannot be overwritten");
        topology["GLOBAL"] = new ProxyTopologyEntry { Type = "Selector", Selected = "b", Choices = new[] { "a", "b" } };
        Equal("GLOBAL", MonitorSelectionBinding.Resolve(topology, "PROXY", "global").Group,
            "global routing monitors the existing GLOBAL selector");
        Throws<InvalidOperationException>(() => MonitorSelectionBinding.Resolve(topology, "PROXY", "direct"), "direct routing cannot be overridden automatically");
        using (var f = new RecoveryFixture())
        {
            var client = new ExistingSelectorClient();
            var probe = new RecoveryProbe { Client = client.Inner };
            var config = FastWorkerConfiguration(f.Root);
            config.SharedGroup = "🚀 节点选择";
            config.GeneralGroup = "";
            var worker = new MonitorWorker(config, client, probe,
                new BoundedLogger(Path.Combine(f.Root, "existing-group.log"), 100000), f.Clock,
                new RecoveryExitProbe(), null, () => new RuntimeSnapshot(true, "", "verge-mihomo", false));
            Equal("current", worker.Run(f.Preferences).ActualNode, "healthy worker works without unified stable group");
            probe.Result = (node, service, visit) => node == "current" && service == ServiceKind.ChatGPT
                ? ProbeResult.ServiceFailure("failed") : ProbeResult.Success(100);
            worker.Run(f.Preferences); worker.Run(f.Preferences);
            Equal("node-01", client.Inner.Current, "confirmed recovery writes the existing PROXY group directly");
        }
    }
}
