using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class RankedOpportunitySelection
{
    public RankedOpportunitySelection(string node, CandidateScanResult scan,
        double responseMilliseconds, int rank, int rechecked,
        IDictionary<string, string> diagnostics = null)
    {
        Node = node;
        Scan = scan;
        ResponseMilliseconds = responseMilliseconds;
        Rank = rank;
        Rechecked = rechecked;
        Diagnostics = diagnostics ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string Node { get; private set; }
    public CandidateScanResult Scan { get; private set; }
    public double ResponseMilliseconds { get; private set; }
    public int Rank { get; private set; }
    public int Rechecked { get; private set; }
    public IDictionary<string, string> Diagnostics { get; private set; }
}

internal static class RankedOpportunitySelector
{
    public static RankedOpportunitySelection Select(IList<CandidateLatencyMeasurement> ranking,
        double baseline, IEnumerable<ServiceKind> requiredServices,
        Func<string, CandidateScanResult> scanFull, double minimumImprovementMilliseconds = 0)
    {
        if (ranking == null) throw new ArgumentNullException("ranking");
        if (scanFull == null) throw new ArgumentNullException("scanFull");
        var diagnostics = ranking.ToDictionary(x => x.Node,
            x => CandidateDecisionText.Initial(x), StringComparer.Ordinal);
        int rechecked = 0;
        for (int index = 0; index < ranking.Count; index++)
        {
            CandidateLatencyMeasurement candidate = ranking[index];
            if (ResponseMilliseconds(candidate) >= baseline)
            {
                for (int rest = index; rest < ranking.Count; rest++)
                    if (diagnostics[ranking[rest].Node] == "等待完整复检")
                        diagnostics[ranking[rest].Node] = "网站实测未优于当前节点";
                break;
            }
            CandidateScanResult fullScan = scanFull(candidate.Node);
            rechecked++;
            double response = WebsitePriorityLatency.Primary(fullScan);
            if (!OpportunityOptimizationPolicy.IsPerformanceComparable(fullScan, requiredServices))
            {
                diagnostics[candidate.Node] = CandidateDecisionText.FullRecheck(fullScan, requiredServices);
                continue;
            }
            if (response >= baseline)
            {
                diagnostics[candidate.Node] = "完整复检后未优于当前节点";
                continue;
            }
            if (baseline - response < Math.Max(0, minimumImprovementMilliseconds))
            {
                diagnostics[candidate.Node] = "完整复检通过，但稳定期内改善不足 " +
                    Math.Max(0, minimumImprovementMilliseconds).ToString("F0") + " ms";
                continue;
            }
            diagnostics[candidate.Node] = "完整复检通过";
            for (int rest = index + 1; rest < ranking.Count; rest++)
                if (diagnostics[ranking[rest].Node] == "等待完整复检")
                    diagnostics[ranking[rest].Node] = "已有更优候选";
            return new RankedOpportunitySelection(candidate.Node, fullScan, response,
                index + 1, rechecked, diagnostics);
        }
        return new RankedOpportunitySelection(null, null, Double.MaxValue, 0, rechecked,
            diagnostics);
    }

    public static double ResponseMilliseconds(CandidateLatencyMeasurement candidate)
    {
        return WebsitePriorityLatency.Primary(candidate);
    }
}
