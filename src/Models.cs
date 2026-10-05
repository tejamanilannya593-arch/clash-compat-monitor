using System.Collections.Generic;
using System.Collections.ObjectModel;

public sealed class CandidateNode
{
    public CandidateNode(string name, double? multiplier)
        : this(name, multiplier, "", NodeIdentityStrength.SessionOnly)
    {
    }

    public CandidateNode(string name, double? multiplier, string nodeId, NodeIdentityStrength identityStrength)
    {
        Name = name;
        Multiplier = multiplier;
        NodeId = nodeId ?? "";
        IdentityStrength = identityStrength;
    }

    public string Name { get; private set; }
    public double? Multiplier { get; private set; }
    public string NodeId { get; private set; }
    public NodeIdentityStrength IdentityStrength { get; private set; }
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
    Epic,
    ZLibraryWeb
}

public enum ProbeFailureKind { None, Region, Service, Transient, Unverified, Partial, LoginRedirect }
public enum CandidateHealth { Unknown, Compatible, BasicCompatible, RegionBlocked, ServiceFailed, Transient }
public enum ServiceOutcome { Unknown, Success, Failure }

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
    public static ProbeResult LoginRedirect(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(true, ProbeFailureKind.LoginRedirect, detail, elapsedMilliseconds); }
    public static ProbeResult RegionFailure(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(false, ProbeFailureKind.Region, detail, elapsedMilliseconds); }
    public static ProbeResult ServiceFailure(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(false, ProbeFailureKind.Service, detail, elapsedMilliseconds); }
    public static ProbeResult TransientFailure(string detail, long elapsedMilliseconds = 0) { return new ProbeResult(false, ProbeFailureKind.Transient, detail, elapsedMilliseconds); }
}

public sealed class ServiceObservation
{
    public ServiceKind Service { get; set; }
    public ServiceOutcome Outcome { get; set; }
    public ProbeFailureKind FailureKind { get; set; }
    public string Detail { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public System.DateTime ObservedUtc { get; set; }
    public string ExitFingerprint { get; set; }
    public string ExitCountryCode { get; set; }
    public long? ExitAsn { get; set; }
    public bool CountedForNodeHealth { get; set; }

    public static ServiceObservation FromProbe(ServiceKind service, ProbeResult result,
        System.DateTime observedUtc, string exitFingerprint, string exitCountryCode, long? exitAsn)
    {
        if (result == null) throw new System.ArgumentNullException("result");
        ServiceOutcome outcome = result.FailureKind == ProbeFailureKind.Unverified ||
            result.FailureKind == ProbeFailureKind.Partial ? ServiceOutcome.Unknown :
            result.Passed ? ServiceOutcome.Success : ServiceOutcome.Failure;
        return new ServiceObservation {
            Service = service,
            Outcome = outcome,
            FailureKind = result.FailureKind,
            Detail = result.Detail ?? "",
            ElapsedMilliseconds = result.ElapsedMilliseconds,
            ObservedUtc = observedUtc,
            ExitFingerprint = exitFingerprint ?? "",
            ExitCountryCode = exitCountryCode ?? "",
            ExitAsn = exitAsn,
            CountedForNodeHealth = true
        };
    }

    public ServiceObservation WithNodeHealthCounting(bool counted)
    {
        return new ServiceObservation {
            Service = Service,
            Outcome = Outcome,
            FailureKind = FailureKind,
            Detail = Detail,
            ElapsedMilliseconds = ElapsedMilliseconds,
            ObservedUtc = ObservedUtc,
            ExitFingerprint = ExitFingerprint,
            ExitCountryCode = ExitCountryCode,
            ExitAsn = ExitAsn,
            CountedForNodeHealth = counted
        };
    }
}

public sealed class CandidateScanResult
{
    public CandidateScanResult(string name, CandidateHealth health, ServiceKind? failedService, string detail,
        long totalMilliseconds = 0, int probeCount = 0, IDictionary<ServiceKind, ProbeResult> serviceResults = null,
        string exitFingerprint = null, string exitCountryCode = null,
        IDictionary<ServiceKind, ServiceObservation> serviceObservations = null, long? exitAsn = null)
    {
        Name = name;
        Health = health;
        FailedService = failedService;
        Detail = detail;
        TotalMilliseconds = totalMilliseconds;
        ProbeCount = probeCount;
        ExitFingerprint = exitFingerprint ?? "";
        ExitCountryCode = exitCountryCode ?? "";
        ExitAsn = exitAsn;
        ServiceResults = new ReadOnlyDictionary<ServiceKind, ProbeResult>(
            new Dictionary<ServiceKind, ProbeResult>(serviceResults ?? new Dictionary<ServiceKind, ProbeResult>()));
        ServiceObservations = new ReadOnlyDictionary<ServiceKind, ServiceObservation>(
            new Dictionary<ServiceKind, ServiceObservation>(serviceObservations ??
                new Dictionary<ServiceKind, ServiceObservation>()));
    }
    public string Name { get; private set; }
    public CandidateHealth Health { get; private set; }
    public ServiceKind? FailedService { get; private set; }
    public string Detail { get; private set; }
    public long TotalMilliseconds { get; private set; }
    public int ProbeCount { get; private set; }
    public string ExitFingerprint { get; private set; }
    public string ExitCountryCode { get; private set; }
    public long? ExitAsn { get; private set; }
    public IDictionary<ServiceKind, ProbeResult> ServiceResults { get; private set; }
    public IDictionary<ServiceKind, ServiceObservation> ServiceObservations { get; private set; }
}
