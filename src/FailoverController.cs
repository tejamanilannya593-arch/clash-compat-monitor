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
                (x.Health == CandidateHealth.Compatible || x.Health == CandidateHealth.BasicCompatible) &&
                x.CooldownUntilUtc <= clock.UtcNow)
            .OrderByDescending(x => x.CheckedUtc).FirstOrDefault();
        return target == null ? new FailoverDecision(false, null, "no compatible candidate") : new FailoverDecision(true, target.Name, totalDisconnect ? "total disconnect" : "confirmed failure");
    }

    public FailoverDecision DecideQuality(double currentScore, double targetScore, bool currentDisconnected, bool targetFreshlyVerified)
    {
        if (!targetFreshlyVerified) return new FailoverDecision(false, null, "target requires fresh verification");
        if (!currentDisconnected && clock.UtcNow - lastSwitchUtc < minimumHold)
            return new FailoverDecision(false, null, "minimum hold");
        if (currentDisconnected) return new FailoverDecision(true, null, "current disconnected");
        bool materiallyBetter = targetScore >= currentScore * 1.20;
        return new FailoverDecision(materiallyBetter, null, materiallyBetter ? "quality improved by at least 20 percent" : "quality difference below threshold");
    }

    public void RecordSwitch()
    {
        lastSwitchUtc = clock.UtcNow;
        consecutiveFailures = 0;
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
