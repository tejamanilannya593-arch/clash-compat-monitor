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
        var result = new List<CandidateNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (names == null) return result;

        foreach (string name in names)
        {
            if (String.IsNullOrWhiteSpace(name) || Reserved.Contains(name)) continue;
            if (!seen.Add(name)) continue;
            result.Add(new CandidateNode(name, QualityScorer.ParseMultiplier(name)));
        }
        return result;
    }
}
