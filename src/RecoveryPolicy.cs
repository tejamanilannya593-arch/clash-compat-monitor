using System;
using System.Collections.Generic;
using System.Linq;

public enum RecoveryEvidence { Unknown, Healthy, Failed }

public interface IRecoveryConnectivityClient
{
    RecoveryEvidence CheckConnectivity(string node, string url, int timeoutMilliseconds, out int milliseconds);
}

// Persisted independently of legacy optimization/observation transactions.
public sealed class RecoveryState
{
    public string Node { get; set; }
    public string Scope { get; set; }
    public int BasicFailures { get; set; }
    public int ChatGptFailures { get; set; }
    public int GeminiFailures { get; set; }
    public int SlowChecks { get; set; }
    public DateTime LastCheckUtc { get; set; }
    public DateTime CooldownUntilUtc { get; set; }
    public string PendingOriginal { get; set; }
    public string PendingWritten { get; set; }
    public string PendingPreviousWrite { get; set; }
    public string PendingSelectorKey { get; set; }

    public void ClearTransaction()
    { PendingOriginal = null; PendingWritten = null; PendingPreviousWrite = null; PendingSelectorKey = null; }

    public void ResetCounts(string node, string scope)
    {
        Node = node; Scope = scope;
        BasicFailures = ChatGptFailures = GeminiFailures = SlowChecks = 0;
        LastCheckUtc = DateTime.MinValue;
    }

    public void Observe(string node, string scope, RecoveryCheck check, DateTime now, int intervalSeconds)
    {
        if (Node != node || Scope != scope || now < LastCheckUtc ||
            now - LastCheckUtc > TimeSpan.FromSeconds(intervalSeconds * 2 + 180)) ResetCounts(node, scope);
        BasicFailures = Increment(BasicFailures, check.Basic);
        ChatGptFailures = Increment(ChatGptFailures, check.Service(ServiceKind.ChatGPT));
        GeminiFailures = Increment(GeminiFailures, check.Service(ServiceKind.Gemini));
        SlowChecks = check.Healthy && check.ResponseMilliseconds > 500 ? Math.Min(100, SlowChecks + 1) : 0;
        LastCheckUtc = now;
    }

    private static int Increment(int count, RecoveryEvidence evidence)
    { return evidence == RecoveryEvidence.Failed ? Math.Min(100, count + 1) : 0; }

    public bool NeedsFailureConfirmation(int threshold)
    { return BasicFailures >= threshold || ChatGptFailures >= threshold || GeminiFailures >= threshold; }

    public bool ConfirmsFailure(RecoveryCheck confirmation, int threshold)
    {
        return (BasicFailures >= threshold && confirmation.Basic == RecoveryEvidence.Failed) ||
            (ChatGptFailures >= threshold && confirmation.Service(ServiceKind.ChatGPT) == RecoveryEvidence.Failed) ||
            (GeminiFailures >= threshold && confirmation.Service(ServiceKind.Gemini) == RecoveryEvidence.Failed);
    }
}

public sealed class RecoveryCheck
{
    public CandidateScanResult Scan { get; set; }
    public RecoveryEvidence Basic { get; set; }
    public int BasicMilliseconds { get; set; }
    public RecoveryEvidence Service(ServiceKind service)
    {
        ProbeResult result;
        if (Scan == null || !Scan.ServiceResults.TryGetValue(service, out result) || result == null ||
            result.FailureKind == ProbeFailureKind.Unverified || result.FailureKind == ProbeFailureKind.Partial)
            return RecoveryEvidence.Unknown;
        return result.Passed ? RecoveryEvidence.Healthy : RecoveryEvidence.Failed;
    }
    public bool Healthy { get { return Basic == RecoveryEvidence.Healthy &&
        Service(ServiceKind.ChatGPT) == RecoveryEvidence.Healthy && Service(ServiceKind.Gemini) == RecoveryEvidence.Healthy; } }
    public double ResponseMilliseconds
    {
        get { return Math.Max(BasicMilliseconds, CoreWebsitePolicy.Required.Select(service => {
            ProbeResult result;
            return Scan != null && Scan.ServiceResults.TryGetValue(service, out result) && result != null
                ? (double)result.ElapsedMilliseconds : 0;
        }).DefaultIfEmpty(0).Max()); }
    }
    public string RejectionReason
    {
        get { return String.Join("; ", new[] { "基础网络=" + Basic,
            "ChatGPT=" + Service(ServiceKind.ChatGPT), "Gemini=" + Service(ServiceKind.Gemini) }); }
    }
    public CandidateScanResult DisplayScan()
    {
        CandidateHealth health = Healthy ? CandidateHealth.Compatible :
            Basic == RecoveryEvidence.Failed || CoreWebsitePolicy.Required.Any(x => Service(x) == RecoveryEvidence.Failed)
                ? CandidateHealth.ServiceFailed : CandidateHealth.Unknown;
        return new CandidateScanResult(Scan.Name, health,
            CoreWebsitePolicy.Required.Where(x => Service(x) == RecoveryEvidence.Failed).Select(x => (ServiceKind?)x).FirstOrDefault(),
            RejectionReason + "; 基础响应=" + BasicMilliseconds + " ms", Scan.TotalMilliseconds, Scan.ProbeCount,
            Scan.ServiceResults, Scan.ExitFingerprint, Scan.ExitCountryCode, Scan.ServiceObservations, Scan.ExitAsn);
    }
}
