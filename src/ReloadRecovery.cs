using System;
using System.Collections.Generic;
using System.Linq;

public static class ReloadRecovery
{
    public static string ChooseTarget(bool reloadDetected, HealthState state,
        IEnumerable<CandidateNode> candidates, DateTime nowUtc, TimeSpan freshness)
    {
        if (!reloadDetected || state == null || String.IsNullOrEmpty(state.PreferredNode)) return null;
        DateTime verifiedUtc = state.PreferredNodeVerifiedUtc;
        if (verifiedUtc == DateTime.MinValue || verifiedUtc > nowUtc || nowUtc - verifiedUtc > freshness) return null;
        if (!(candidates ?? Enumerable.Empty<CandidateNode>()).Any(x => x.Name == state.PreferredNode)) return null;
        NodeHealthRecord record;
        if (!state.Records.TryGetValue(state.PreferredNode, out record)) return null;
        bool usable = record.Health == CandidateHealth.Compatible || record.Health == CandidateHealth.BasicCompatible;
        return usable && record.CooldownUntilUtc <= nowUtc ? state.PreferredNode : null;
    }
}
