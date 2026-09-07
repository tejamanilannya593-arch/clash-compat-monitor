using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public static class CandidateCatalog
{
    private static readonly Regex Rate = new Regex(@"\|\s*([0-5])x\s*$", RegexOptions.CultureInvariant);

    public static IList<CandidateNode> Filter(IEnumerable<string> names)
    {
        var result = new List<CandidateNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (names == null) return result;

        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name) || !StartsWithRegionalFlag(name)) continue;
            if (name.IndexOf("中国大陆", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("香港", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("澳门", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("台湾", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("Taiwan", StringComparison.OrdinalIgnoreCase) >= 0) continue;

            Match match = Rate.Match(name);
            int multiplier;
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out multiplier) || multiplier > 3) continue;
            if (!seen.Add(name)) continue;
            result.Add(new CandidateNode(name, multiplier));
        }
        return result;
    }

    private static bool StartsWithRegionalFlag(string value)
    {
        return value.Length >= 4 &&
               value[0] == '\uD83C' && value[1] >= '\uDDE6' && value[1] <= '\uDDFF' &&
               value[2] == '\uD83C' && value[3] >= '\uDDE6' && value[3] <= '\uDDFF';
    }
}
