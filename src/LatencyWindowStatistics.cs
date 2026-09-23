using System;
using System.Collections.Generic;
using System.Linq;

public sealed class LatencyWindowSummary
{
    public LatencyWindowSummary(int count, double medianMilliseconds, double p75Milliseconds,
        double p90Milliseconds, double maximumMilliseconds)
    {
        Count = count;
        MedianMilliseconds = medianMilliseconds;
        P75Milliseconds = p75Milliseconds;
        P90Milliseconds = p90Milliseconds;
        MaximumMilliseconds = maximumMilliseconds;
    }

    public int Count { get; private set; }
    public double MedianMilliseconds { get; private set; }
    public double P75Milliseconds { get; private set; }
    public double P90Milliseconds { get; private set; }
    public double MaximumMilliseconds { get; private set; }
    public double JitterMilliseconds { get { return Math.Max(0, P90Milliseconds - MedianMilliseconds); } }
}

public static class LatencyWindowStatistics
{
    public const int MinimumSamples = 5;
    public const int MaximumSamples = 10;

    public static LatencyWindowSummary Summarize(IEnumerable<double> responses)
    {
        List<double> sorted = (responses ?? Enumerable.Empty<double>())
            .Where(x => !Double.IsNaN(x) && !Double.IsInfinity(x) && x > 0)
            .TakeLastCompat(MaximumSamples)
            .OrderBy(x => x)
            .ToList();
        if (sorted.Count == 0) return new LatencyWindowSummary(0, 0, 0, 0, 0);
        return new LatencyWindowSummary(sorted.Count, Median(sorted), Percentile(sorted, 0.75),
            Percentile(sorted, 0.90), sorted[sorted.Count - 1]);
    }

    private static double Median(IList<double> sorted)
    {
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] :
            (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }

    private static double Percentile(IList<double> sorted, double percentile)
    {
        int index = Math.Max(0, Math.Min(sorted.Count - 1, (int)Math.Ceiling(percentile * sorted.Count) - 1));
        return sorted[index];
    }
}
