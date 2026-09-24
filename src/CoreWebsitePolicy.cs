using System;
using System.Collections.Generic;
using System.Linq;

internal static class CoreWebsitePolicy
{
    public static readonly ServiceKind[] Required = {
        ServiceKind.ChatGPT, ServiceKind.SteamApi, ServiceKind.Google
    };

    public static List<ServiceKind> Normalize(IEnumerable<ServiceKind> selected)
    {
        return Required.Concat((selected ?? Enumerable.Empty<ServiceKind>())
                .Where(service => service != ServiceKind.Gemini))
            .Distinct().ToList();
    }

    public static bool AllReachable(CandidateScanResult scan)
    {
        if (scan == null || scan.ServiceResults == null) return false;
        return Required.All(service => {
            ProbeResult result;
            return scan.ServiceResults.TryGetValue(service, out result) && result != null && result.Passed;
        });
    }
}
