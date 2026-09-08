using System.Collections.Generic;
using System.Collections.ObjectModel;

public sealed class CandidateNode
{
    public CandidateNode(string name, int multiplier)
    {
        Name = name;
        Multiplier = multiplier;
    }

    public string Name { get; private set; }
    public int Multiplier { get; private set; }
}

public enum ServiceKind
{
    ChatGPT,
    Gemini,
    Google,
    GitHub,
    SteamStore,
    SteamCommunity,
    SteamApi,
    Discord,
    Spotify,
    Epic
}

public enum ProbeFailureKind { None, Region, Service, Transient, Unverified, Partial }
public enum CandidateHealth { Unknown, Compatible, BasicCompatible, RegionBlocked, ServiceFailed, Transient }

public sealed class ProbeResult
{
    private ProbeResult(bool passed, ProbeFailureKind failureKind, string detail, long elapsedMilliseconds)
    {
        Passed = passed;
        FailureKind = failureKind;
        Detail = detail;
        ElapsedMilliseconds = elapsedMilliseconds;
    }
    public bool Passed { get; private set; }
    public ProbeFailureKind FailureKind { get; private set; }
    public string Detail { get; private set; }
    public long ElapsedMilliseconds { get; private set; }
    public static ProbeResult Success(long elapsedMilliseconds = 0) { return new ProbeResult(true, ProbeFailureKind.None, "ok", elapsedMilliseconds); }
    public static ProbeResult ReachableChallenge(long elapsedMilliseconds = 0) { return Partial("验证码页面可达，未验证平台功能", elapsedMilliseconds); }
    public static ProbeResult Unverified(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(false, ProbeFailureKind.Unverified, detail, elapsedMilliseconds); }
    public static ProbeResult Partial(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(true, ProbeFailureKind.Partial, detail, elapsedMilliseconds); }
    public static ProbeResult RegionFailure(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(false, ProbeFailureKind.Region, detail, elapsedMilliseconds); }
    public static ProbeResult ServiceFailure(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(false, ProbeFailureKind.Service, detail, elapsedMilliseconds); }
    public static ProbeResult TransientFailure(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(false, ProbeFailureKind.Transient, detail, elapsedMilliseconds); }
}

public sealed class CandidateScanResult
{
    public CandidateScanResult(string name, CandidateHealth health, ServiceKind? failedService, string detail,
        long totalMilliseconds = 0, int probeCount = 0, IDictionary<ServiceKind, ProbeResult> serviceResults = null)
    {
        Name = name;
        Health = health;
        FailedService = failedService;
        Detail = detail;
        TotalMilliseconds = totalMilliseconds;
        ProbeCount = probeCount;
        ServiceResults = new ReadOnlyDictionary<ServiceKind, ProbeResult>(
            new Dictionary<ServiceKind, ProbeResult>(serviceResults ?? new Dictionary<ServiceKind, ProbeResult>()));
    }
    public string Name { get; private set; }
    public CandidateHealth Health { get; private set; }
    public ServiceKind? FailedService { get; private set; }
    public string Detail { get; private set; }
    public long TotalMilliseconds { get; private set; }
    public int ProbeCount { get; private set; }
    public IDictionary<ServiceKind, ProbeResult> ServiceResults { get; private set; }
}
