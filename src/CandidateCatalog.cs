using System;
using System.Collections.Generic;

public static class CandidateCatalog
{
    private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE", "GLOBAL",
        "🌐 统一稳定节点", "🧪 兼容性探测"
    };

    public static IList<CandidateNode> Filter(IEnumerable<string> names)
    {
        return Filter(names, null);
    }

    public static IList<CandidateNode> Filter(IEnumerable<string> names, IDictionary<string, string> runtimeTypes)
    {
        var result = new List<CandidateNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (names == null) return result;

        foreach (string name in names)
        {
            if (String.IsNullOrWhiteSpace(name) || Reserved.Contains(name)) continue;
            string kind;
            if (runtimeTypes != null && runtimeTypes.TryGetValue(name, out kind) && IsNonLeaf(kind)) continue;
            if (!seen.Add(name)) continue;
            result.Add(new CandidateNode(name, QualityScorer.ParseMultiplier(name)));
        }
        return result;
    }

    private static bool IsNonLeaf(string kind)
    {
        switch ((kind ?? "").Trim().ToLowerInvariant())
        {
            case "direct": case "reject": case "reject-drop": case "pass": case "compatible":
            case "selector": case "select": case "urltest": case "url-test":
            case "fallback": case "loadbalance": case "load-balance": case "relay":
                return true;
            default: return false;
        }
    }
}
