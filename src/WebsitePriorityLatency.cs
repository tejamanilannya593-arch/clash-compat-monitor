using System;
using System.Collections.Generic;
using System.Linq;

internal static class WebsitePriorityLatency
{
    public static double Primary(CandidateScanResult scan)
    {
        return scan == null ? Double.MaxValue : Single(scan.ServiceResults, ServiceKind.ChatGPT);
    }

    public static double Secondary(CandidateScanResult scan)
    {
        return scan == null ? Double.MaxValue : Pair(
            scan.ServiceResults, ServiceKind.SteamApi, ServiceKind.Google);
    }

    public static double Primary(CandidateLatencyMeasurement candidate)
    {
        return candidate == null ? Double.MaxValue : Single(candidate.Services, ServiceKind.ChatGPT);
    }

    public static double Primary(IEnumerable<ServiceMeasurement> services)
    {
        return Single(services, ServiceKind.ChatGPT);
    }

    public static double Secondary(CandidateLatencyMeasurement candidate)
    {
        return candidate == null ? Double.MaxValue : Pair(
            candidate.Services, ServiceKind.SteamApi, ServiceKind.Google);
    }

    private static double Pair(IDictionary<ServiceKind, ProbeResult> results,
        ServiceKind first, ServiceKind second)
    {
        ProbeResult left;
        ProbeResult right;
        if (results == null || !results.TryGetValue(first, out left) ||
            !results.TryGetValue(second, out right) || left == null || right == null ||
            !left.Passed || !right.Passed || left.ElapsedMilliseconds <= 0 ||
            right.ElapsedMilliseconds <= 0) return Double.MaxValue;
        return Math.Max(left.ElapsedMilliseconds, right.ElapsedMilliseconds);
    }

    private static double Single(IDictionary<ServiceKind, ProbeResult> results, ServiceKind service)
    {
        ProbeResult result;
        return results != null && results.TryGetValue(service, out result) && result != null &&
            result.Passed && result.ElapsedMilliseconds > 0
            ? result.ElapsedMilliseconds : Double.MaxValue;
    }

    private static double Single(IEnumerable<ServiceMeasurement> services, ServiceKind service)
    {
        ServiceMeasurement result = services == null ? null : services.FirstOrDefault(x => x.Service == service);
        return result != null && result.Available && result.Milliseconds > 0
            ? result.Milliseconds : Double.MaxValue;
    }

    private static double Pair(IEnumerable<ServiceMeasurement> services,
        ServiceKind first, ServiceKind second)
    {
        if (services == null) return Double.MaxValue;
        ServiceMeasurement left = services.FirstOrDefault(x => x.Service == first);
        ServiceMeasurement right = services.FirstOrDefault(x => x.Service == second);
        if (left == null || right == null || !left.Available || !right.Available ||
            left.Milliseconds <= 0 || right.Milliseconds <= 0) return Double.MaxValue;
        return Math.Max(left.Milliseconds, right.Milliseconds);
    }
}
