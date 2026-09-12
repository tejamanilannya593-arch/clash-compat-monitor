using System;

public enum AccountVerificationMethod
{
    LegacyManual = 0,
    BrowserConversation = 1
}

public static class BrowserConversationProof
{
    public const int CurrentProtocolVersion = 1;
    public static readonly TimeSpan Validity = TimeSpan.FromDays(30);
    public static readonly TimeSpan AutomaticFailureCooldown = TimeSpan.FromHours(6);
}
