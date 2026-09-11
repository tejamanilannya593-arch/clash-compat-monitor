using System;
using System.Collections.Generic;
using System.Linq;

public interface IClock { DateTime UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTime UtcNow { get { return DateTime.UtcNow; } } }

public sealed class FailoverDecision
{
    public FailoverDecision(bool shouldSwitch, string target, string reason)
    {
        ShouldSwitch = shouldSwitch;
        Target = target;
        Reason = reason;
    }
    public bool ShouldSwitch { get; private set; }
    public string Target { get; private set; }
    public string Reason { get; private set; }
}

public static class SwitchModePolicy
{
    public static bool AllowsAutomaticSwitch(bool currentCompatible, bool performanceOptimization)
    {
        return !currentCompatible || performanceOptimization;
    }
}

public sealed class FailoverController
{
    private readonly IClock clock;
    private readonly TimeSpan minimumHold;
    private int consecutiveFailures;
    private DateTime lastSwitchUtc = DateTime.MinValue;

    public FailoverController(IClock clock, TimeSpan minimumHold)
    {
        this.clock = clock;
        this.minimumHold = minimumHold;
    }

    public FailoverDecision Decide(bool currentHealthy, bool totalDisconnect, string current, IEnumerable<NodeHealthRecord> records)
    {
        if (currentHealthy)
        {
            consecutiveFailures = 0;
            return new FailoverDecision(false, null, "current healthy");
        }
        consecutiveFailures++;
        if (!totalDisconnect && clock.UtcNow - lastSwitchUtc < minimumHold) return new FailoverDecision(false, null, "minimum hold");
        if (!totalDisconnect && consecutiveFailures < 2) return new FailoverDecision(false, null, "awaiting confirmation");
        NodeHealthRecord target = (records ?? Enumerable.Empty<NodeHealthRecord>())
            .Where(x => x.Name != current &&
                x.Health == CandidateHealth.Compatible &&
                x.CooldownUntilUtc <= clock.UtcNow)
            .OrderByDescending(x => x.CheckedUtc).FirstOrDefault();
        return target == null ? new FailoverDecision(false, null, "no compatible candidate") : new FailoverDecision(true, target.Name, totalDisconnect ? "total disconnect" : "confirmed failure");
    }

    public FailoverDecision DecideQuality(double currentScore, double targetScore, bool currentDisconnected, bool targetFreshlyVerified,
        bool targetProvenStable, IEnumerable<double> currentResponses, IEnumerable<double> targetResponses,
        CandidateScanResult targetScan)
    {
        if (!targetFreshlyVerified) return new FailoverDecision(false, null, "target requires fresh verification");
        if (!ServiceEvidencePolicy.CanEmergencySwitch(targetScan))
            return new FailoverDecision(false, null, "target requires strict service evidence");
        if (currentDisconnected) return new FailoverDecision(true, null, "current disconnected");
        if (!QualityPolicy.CurrentNeedsOptimization(currentResponses))
            return new FailoverDecision(false, null, "current response not persistently slow");
        if (!targetProvenStable) return new FailoverDecision(false, null, "target requires proven stability");
        if (!QualityPolicy.CandidateLatencyIsPreferred(targetResponses))
            return new FailoverDecision(false, null, "target response history not preferred");
        if (!QualityPolicy.ServicesWithinLimit(targetScan))
            return new FailoverDecision(false, null, "target service response exceeds limit");
        if (clock.UtcNow - lastSwitchUtc < minimumHold)
            return new FailoverDecision(false, null, "minimum hold");
        bool materiallyBetter = targetScore >= currentScore * 1.20;
        return new FailoverDecision(materiallyBetter, null, materiallyBetter ? "quality improved by at least 20 percent" : "quality difference below threshold");
    }

    public void RecordSwitch()
    {
        lastSwitchUtc = clock.UtcNow;
        consecutiveFailures = 0;
    }
}

public static class QualityPolicy
{
    public const double PreferredResponseMilliseconds = 500.0;
    public const double OptimizationResponseMilliseconds = 800.0;
    public const double MaximumServiceResponseMilliseconds = 1500.0;
    public const double MaximumJitterMilliseconds = 150.0;

    public static string LatencyBand(double milliseconds)
    {
        if (Double.IsNaN(milliseconds) || Double.IsInfinity(milliseconds) || milliseconds < 0) return "待测";
        if (milliseconds <= PreferredResponseMilliseconds) return "优秀";
        if (milliseconds <= OptimizationResponseMilliseconds) return "良好";
        if (milliseconds <= MaximumServiceResponseMilliseconds) return "可连接但偏慢";
        return "不适合自动寻优";
    }

    public static bool CurrentNeedsOptimization(IEnumerable<double> responses)
    {
        List<double> recent = Valid(responses).TakeLastCompat(3).OrderBy(x => x).ToList();
        return recent.Count == 3 && Median(recent) > OptimizationResponseMilliseconds;
    }

    public static bool CandidateLatencyIsPreferred(IEnumerable<double> responses)
    {
        List<double> recent = Valid(responses).TakeLastCompat(5).OrderBy(x => x).ToList();
        if (recent.Count < 5) return false;
        double median = Median(recent);
        double maximum = recent[recent.Count - 1];
        List<double> deviations = recent.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToList();
        return median <= OptimizationResponseMilliseconds && maximum <= MaximumServiceResponseMilliseconds &&
            Median(deviations) <= MaximumJitterMilliseconds;
    }

    public static bool ServicesWithinLimit(CandidateScanResult scan)
    {
        return scan != null && !scan.ServiceResults.Values.Any(x => x.ElapsedMilliseconds > MaximumServiceResponseMilliseconds);
    }

    private static IEnumerable<double> Valid(IEnumerable<double> responses)
    {
        return (responses ?? Enumerable.Empty<double>()).Where(x => !Double.IsNaN(x) && !Double.IsInfinity(x) && x > 0);
    }

    private static double Median(IList<double> sorted)
    {
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] :
            (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }
}

internal static class EnumerableCompatibility
{
    public static IEnumerable<T> TakeLastCompat<T>(this IEnumerable<T> values, int count)
    {
        Queue<T> queue = new Queue<T>();
        foreach (T value in values)
        {
            if (queue.Count == count) queue.Dequeue();
            queue.Enqueue(value);
        }
        return queue;
    }
}

public static class HealthPolicy
{
    public static DateTime CooldownUntil(CandidateHealth health, DateTime now)
    {
        if (health == CandidateHealth.Transient) return now.AddMinutes(5);
        if (health == CandidateHealth.ServiceFailed || health == CandidateHealth.RegionBlocked) return now.AddMinutes(30);
        return now;
    }
}
