using System;
using System.Linq;

public static class SelectorFollower
{
    public static bool SynchronizeVerified(IMihomoClient mihomo, string sourceGroup,
        string targetGroup, CandidateScanResult scan)
    {
        if (!ServiceEvidencePolicy.CanHold(scan) || mihomo == null ||
            !String.Equals(scan.Name, mihomo.GetSelected(sourceGroup), StringComparison.Ordinal)) return false;
        return Synchronize(mihomo, sourceGroup, targetGroup);
    }

    public static bool Synchronize(IMihomoClient mihomo, string sourceGroup, string targetGroup)
    {
        if (mihomo == null) throw new ArgumentNullException("mihomo");
        if (String.IsNullOrWhiteSpace(sourceGroup) || String.IsNullOrWhiteSpace(targetGroup) ||
            String.Equals(sourceGroup, targetGroup, StringComparison.Ordinal)) return false;

        string selected = mihomo.GetSelected(sourceGroup);
        if (String.IsNullOrWhiteSpace(selected)) return false;
        if (!mihomo.GetChoices(targetGroup).Contains(selected, StringComparer.Ordinal)) return false;
        if (String.Equals(mihomo.GetSelected(targetGroup), selected, StringComparison.Ordinal)) return false;
        if (!String.Equals(mihomo.GetSelected(sourceGroup), selected, StringComparison.Ordinal)) return false;

        mihomo.Select(targetGroup, selected);
        if (!String.Equals(mihomo.GetSelected(targetGroup), selected, StringComparison.Ordinal))
            throw new InvalidOperationException("General selector synchronization was not confirmed.");
        return true;
    }
}
