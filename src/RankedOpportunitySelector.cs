using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class RankedOpportunitySelection
{
    public RankedOpportunitySelection(string node, CandidateScanResult scan,
        double responseMilliseconds, int rank, int rechecked)
    {
        Node = node;
        Scan = scan;
        ResponseMilliseconds = responseMilliseconds;
        Rank = rank;
        Rechecked = rechecked;
    }

    public string Node { get; private set; }
    public CandidateScanResult Scan { get; private set; }
    public double ResponseMilliseconds { get; private set; }
    public int Rank { get; private set; }
    public int Rechecked { get; private set; }
}

internal static class RankedOpportunitySelector
{
    public static RankedOpportunitySelection Select(IList<CandidateLatencyMeasurement> ranking,
        double baseline, IEnumerable<ServiceKind> requiredServices,
        Func<string, CandidateScanResult> scanFull)
    {
        if (ranking == null) throw new ArgumentNullException("ranking");
        if (scanFull == null) throw new ArgumentNullException("scanFull");
        int rechecked = 0;
        for (int index = 0; index < ranking.Count; index++)
        {
            CandidateLatencyMeasurement candidate = ranking[index];
            if (ResponseMilliseconds(candidate) >= baseline) break;
            CandidateScanResult fullScan = scanFull(candidate.Node);
            rechecked++;
            double response = QualityMeasurement.ResponseMilliseconds(fullScan, 5000);
            if (!OpportunityOptimizationPolicy.IsPerformanceComparable(fullScan, requiredServices) ||
                response >= baseline) continue;
            return new RankedOpportunitySelection(candidate.Node, fullScan, response,
                index + 1, rechecked);
        }
        return new RankedOpportunitySelection(null, null, Double.MaxValue, 0, rechecked);
    }

    public static double ResponseMilliseconds(CandidateLatencyMeasurement candidate)
    {
        if (candidate == null || candidate.Services.Count == 0 ||
            candidate.Services.Any(x => !x.Available)) return Double.MaxValue;
        List<long> values = candidate.Services.Where(x => x.Available && x.Milliseconds > 0)
            .Select(x => x.Milliseconds).OrderBy(x => x).ToList();
        if (values.Count == 0) return Double.MaxValue;
        int rank = (int)Math.Ceiling(values.Count * 0.75);
        return values[Math.Max(0, rank - 1)];
    }
}
