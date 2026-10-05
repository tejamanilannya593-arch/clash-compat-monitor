using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public static class CandidateCatalog
{
    private static readonly Regex NoticeName = new Regex(
        @"^[^A-Za-z0-9\u3400-\u9FFF]*(?:(?:联系我们|账号信息)[^A-Za-z0-9\u3400-\u9FFF]*$|(?:消息|通知|公告|提示|官网|订阅地址|电报|登录账号|账号|邮箱|客服|套餐|豪华套餐|notice|notification|message|subscription info|telegram|email)\s*[:：]|(?:剩余流量|流量剩余|套餐到期|到期时间|过期时间|下次重置|流量重置)\s*[:：])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSubscriptionNotice(string name)
    {
        return name != null && NoticeName.IsMatch(name);
    }
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
            if (String.IsNullOrWhiteSpace(name) || Reserved.Contains(name) || IsSubscriptionNotice(name)) continue;
            string kind;
            if (runtimeTypes != null && runtimeTypes.TryGetValue(name, out kind) && IsNonLeaf(kind)) continue;
            if (!seen.Add(name)) continue;
            result.Add(new CandidateNode(name, QualityScorer.ParseMultiplier(name)));
        }
        return result;
    }

    public static IList<CandidateNode> Filter(IEnumerable<string> names, IDictionary<string, string> runtimeTypes,
        IDictionary<string, ResolvedNodeIdentity> identities)
    {
        IList<CandidateNode> filtered = Filter(names, runtimeTypes);
        var result = new List<CandidateNode>();
        var strongIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (CandidateNode candidate in filtered)
        {
            ResolvedNodeIdentity identity = null;
            bool strong = identities != null && identities.TryGetValue(candidate.Name, out identity) &&
                identity != null && identity.Strength == NodeIdentityStrength.Strong &&
                NodeIdentity.IsStrong(identity.NodeId);
            var identified = new CandidateNode(candidate.Name, candidate.Multiplier,
                strong ? identity.NodeId : "", strong ? NodeIdentityStrength.Strong : NodeIdentityStrength.SessionOnly);
            if (!strong)
            {
                result.Add(identified);
                continue;
            }
            int index;
            if (!strongIndexes.TryGetValue(identified.NodeId, out index))
            {
                strongIndexes[identified.NodeId] = result.Count;
                result.Add(identified);
            }
            else if (identified.Name.Length < result[index].Name.Length ||
                (identified.Name.Length == result[index].Name.Length &&
                StringComparer.Ordinal.Compare(identified.Name, result[index].Name) < 0))
            {
                result[index] = identified;
            }
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
