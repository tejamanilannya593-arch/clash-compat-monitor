using System;
using System.Collections.Generic;
using System.Linq;

public sealed class AccountVerificationRecord
{
    public string Scope { get; set; }
    public string Node { get; set; }
    public string ExitFingerprint { get; set; }
    public ServiceKind Service { get; set; }
    public bool Passed { get; set; }
    public DateTime VerifiedUtc { get; set; }
    public DateTime RevokedUtc { get; set; }
    public string Reason { get; set; }
    public int EvidenceRuleVersion { get; set; }
    public AccountVerificationMethod Method { get; set; }
    public int ProtocolVersion { get; set; }
}

public static class AccountVerificationMemory
{
    public const int CurrentRuleVersion = 1;
    private static readonly TimeSpan Validity = TimeSpan.FromDays(30);

    public static void Mark(ExperienceData data, string scope, string node, string exitFingerprint,
        ServiceKind service, bool passed, DateTime now, int ruleVersion)
    {
        if (data == null) throw new ArgumentNullException("data");
        if (!IsAccountService(service)) throw new ArgumentException("Only AI account services can be verified.", "service");
        if (data.AccountVerifications == null) data.AccountVerifications = new List<AccountVerificationRecord>();

        data.AccountVerifications.RemoveAll(x => x != null && x.Scope == scope && x.Node == node && x.Service == service);
        data.AccountVerifications.Add(new AccountVerificationRecord
        {
            Scope = scope ?? "",
            Node = node ?? "",
            ExitFingerprint = exitFingerprint ?? "",
            Service = service,
            Passed = passed,
            VerifiedUtc = now,
            RevokedUtc = DateTime.MinValue,
            Reason = passed ? "account-verified" : "account-verification-failed",
            EvidenceRuleVersion = ruleVersion,
            Method = AccountVerificationMethod.LegacyManual,
            ProtocolVersion = 0
        });
    }

    public static void MarkBrowserConversation(ExperienceData data, string scope, string node,
        string exitFingerprint, ServiceKind service, DateTime now, int protocolVersion)
    {
        if (data == null) throw new ArgumentNullException("data");
        if (!IsAccountService(service)) throw new ArgumentException("Only AI account services can be verified.", "service");
        if (data.AccountVerifications == null) data.AccountVerifications = new List<AccountVerificationRecord>();

        data.AccountVerifications.RemoveAll(x => x != null && x.Scope == scope && x.Node == node && x.Service == service);
        data.AccountVerifications.Add(new AccountVerificationRecord
        {
            Scope = scope ?? "",
            Node = node ?? "",
            ExitFingerprint = exitFingerprint ?? "",
            Service = service,
            Passed = true,
            VerifiedUtc = now,
            RevokedUtc = DateTime.MinValue,
            Reason = "browser-conversation-verified",
            EvidenceRuleVersion = CurrentRuleVersion,
            Method = AccountVerificationMethod.BrowserConversation,
            ProtocolVersion = protocolVersion
        });
    }

    public static bool IsBrowserConversationValid(ExperienceData data, string scope, string node,
        string exitFingerprint, ServiceKind service, DateTime now, int protocolVersion)
    {
        AccountVerificationRecord record = FindValid(data, scope, node, exitFingerprint,
            service, now, CurrentRuleVersion);
        return record != null && record.Method == AccountVerificationMethod.BrowserConversation &&
            record.ProtocolVersion == protocolVersion;
    }

    public static bool IsValid(ExperienceData data, string scope, string node, string exitFingerprint,
        ServiceKind service, DateTime now, int ruleVersion)
    {
        return FindValid(data, scope, node, exitFingerprint, service, now, ruleVersion) != null;
    }

    public static AccountVerificationRecord FindValid(ExperienceData data, string scope, string node,
        string exitFingerprint, ServiceKind service, DateTime now, int ruleVersion)
    {
        if (data == null || data.AccountVerifications == null || String.IsNullOrEmpty(exitFingerprint)) return null;
        return data.AccountVerifications.Where(x => x != null && x.Passed && x.RevokedUtc == DateTime.MinValue &&
            x.Scope == (scope ?? "") && x.Node == (node ?? "") && x.ExitFingerprint == exitFingerprint &&
            x.Service == service && x.EvidenceRuleVersion == ruleVersion && x.VerifiedUtc <= now &&
            x.VerifiedUtc > now.Subtract(Validity)).OrderByDescending(x => x.VerifiedUtc).FirstOrDefault();
    }

    public static void Revoke(ExperienceData data, string scope, string node, ServiceKind service,
        DateTime now, string reason)
    {
        if (data == null || data.AccountVerifications == null) return;
        foreach (AccountVerificationRecord record in data.AccountVerifications.Where(x => x != null &&
            x.Scope == (scope ?? "") && x.Node == (node ?? "") && x.Service == service &&
            x.RevokedUtc == DateTime.MinValue))
        {
            record.RevokedUtc = now;
            record.Reason = reason ?? "revoked";
        }
    }

    public static void RevokeForChangedExit(ExperienceData data, string scope, string node,
        string currentExitFingerprint, DateTime now)
    {
        if (data == null || data.AccountVerifications == null || String.IsNullOrEmpty(currentExitFingerprint)) return;
        foreach (AccountVerificationRecord record in data.AccountVerifications.Where(x => x != null &&
            x.Scope == (scope ?? "") && x.Node == (node ?? "") && x.ExitFingerprint != currentExitFingerprint &&
            x.RevokedUtc == DateTime.MinValue))
        {
            record.RevokedUtc = now;
            record.Reason = "exit fingerprint changed";
        }
    }

    private static bool IsAccountService(ServiceKind service)
    {
        return service == ServiceKind.ChatGPT || service == ServiceKind.Gemini;
    }
}
