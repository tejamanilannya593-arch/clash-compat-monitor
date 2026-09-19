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
        if (String.IsNullOrWhiteSpace(selected) || CandidateCatalog.IsSubscriptionNotice(selected)) return false;
        string[] choices = mihomo.GetChoices(targetGroup);
        string target = choices.Contains(sourceGroup, StringComparer.Ordinal) ? sourceGroup : selected;
        if (!choices.Contains(target, StringComparer.Ordinal)) return false;
        if (String.Equals(mihomo.GetSelected(targetGroup), target, StringComparison.Ordinal)) return false;
        if (!String.Equals(mihomo.GetSelected(sourceGroup), selected, StringComparison.Ordinal)) return false;

        mihomo.Select(targetGroup, target);
        if (!String.Equals(mihomo.GetSelected(targetGroup), target, StringComparison.Ordinal))
            throw new InvalidOperationException("General selector synchronization was not confirmed.");
        return true;
    }
}
