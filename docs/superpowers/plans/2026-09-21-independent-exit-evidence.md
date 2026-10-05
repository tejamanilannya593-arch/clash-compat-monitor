# Independent Exit Incident Evidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make public-service incident detection require independent real egress evidence, while preserving raw service probe facts separately from whether they affect node-health history.

**Architecture:** Extend scan results with explicit three-state service observations and optional ASN evidence. Keep Cloudflare trace authoritative for the egress fingerprint/country, enrich ASN lazily through the same candidate proxy with a bounded 60-minute cache, and evaluate incident consensus from distinct fingerprints plus ASN/country diversity. Suppression marks observations as excluded from node history without rewriting their raw outcomes; history consumers receive a derived node-health view.

**Tech Stack:** C#/.NET Framework 4.x WinForms, `HttpClient`, `JavaScriptSerializer`, keyed HMAC-SHA256 fingerprints, PowerShell build/release scripts, existing executable test harness, Node.js Clash tests.

---

## File map

- `src/Models.cs`: define `ServiceOutcome`, `ServiceObservation`, observation mapping, and observation/ASN fields on `CandidateScanResult`.
- `src/CompatibilityScanner.cs`: create one observation for every selected service after final probe and exit evidence are known.
- `src/ExitIdentity.cs`: retain Cloudflare authority, expose safe fingerprinting, add ASN/observation time, and compose cached enrichment.
- `src/ExitNetworkEvidence.cs` (new): parse IPWho evidence, verify it against Cloudflare identity, and persist a bounded 60-minute fingerprint-keyed ASN cache without raw IP.
- `src/Program.cs`: configure the ASN cache path and release identity.
- `src/ServiceIncidentPolicy.cs`: produce explainable consensus decisions and suppress observations by changing only history attribution.
- `src/MonitorWorker.cs`: log safe consensus diagnostics and feed only node-health views into health, quality, stability, and experience history.
- `tests/Tests.cs`: pure, scanner, persistence, consensus, and worker orchestration regression coverage.
- `src/AssemblyInfo.cs`, `scripts/install.ps1`, `package-release.ps1`, `tests/Release.Tests.ps1`, `README.md`, `README.en.md`, `QUICKSTART.md`, `docs/release-notes/v0.7.0-preview.8.md`: non-overwriting preview.8 release.

### Task 1: Add the raw three-state observation model

**Files:**
- Modify: `src/Models.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Register and write failing observation-model tests**

Add `ServiceObservationBehavior();` near the other model-level calls in `Tests.Main`, then add this method:

```csharp
private static void ServiceObservationBehavior()
{
    DateTime observed = new DateTime(2026, 9, 21, 1, 2, 3, DateTimeKind.Utc);
    ServiceObservation success = ServiceObservation.FromProbe(ServiceKind.Google,
        ProbeResult.Success(123), observed, Fingerprint('A'), "SG", 64500);
    Equal(ServiceOutcome.Success, success.Outcome, "definite pass maps to success");
    Equal(true, success.CountedForNodeHealth, "raw pass counts for node health");
    Equal(64500L, success.ExitAsn, "observation carries ASN");

    ServiceObservation failure = ServiceObservation.FromProbe(ServiceKind.ChatGPT,
        ProbeResult.RegionFailure("blocked", 456), observed, Fingerprint('B'), "US", 64501);
    Equal(ServiceOutcome.Failure, failure.Outcome, "definite failure maps to failure");
    Equal(ProbeFailureKind.Region, failure.FailureKind, "failure kind is preserved");

    ServiceObservation partial = ServiceObservation.FromProbe(ServiceKind.Gemini,
        ProbeResult.Partial("reachable only", 78), observed, Fingerprint('C'), "JP", null);
    Equal(ServiceOutcome.Unknown, partial.Outcome, "partial reachability maps to unknown");
    Equal(ProbeFailureKind.Partial, partial.FailureKind, "partial kind is preserved");

    ServiceObservation unverified = ServiceObservation.FromProbe(ServiceKind.Discord,
        ProbeResult.Unverified("not probed"), observed, "", "", null);
    Equal(ServiceOutcome.Unknown, unverified.Outcome, "unverified maps to unknown");
    Equal(false, unverified.WithNodeHealthCounting(false).CountedForNodeHealth,
        "history attribution can be disabled without changing outcome");
    Equal(ServiceOutcome.Unknown, unverified.WithNodeHealthCounting(false).Outcome,
        "history attribution leaves raw outcome unchanged");
}

private static string Fingerprint(char value)
{
    return new string(value, 64);
}
```

- [ ] **Step 2: Run the tests and verify the model is absent**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Expected: compilation fails because `ServiceObservation` and `ServiceOutcome` do not exist.

- [ ] **Step 3: Implement the observation type and extend scan results compatibly**

Add to `src/Models.cs`:

```csharp
public enum ServiceOutcome { Unknown, Success, Failure }

public sealed class ServiceObservation
{
    public ServiceKind Service { get; set; }
    public ServiceOutcome Outcome { get; set; }
    public ProbeFailureKind FailureKind { get; set; }
    public string Detail { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public DateTime ObservedUtc { get; set; }
    public string ExitFingerprint { get; set; }
    public string ExitCountryCode { get; set; }
    public long? ExitAsn { get; set; }
    public bool CountedForNodeHealth { get; set; }

    public static ServiceObservation FromProbe(ServiceKind service, ProbeResult result,
        DateTime observedUtc, string exitFingerprint, string exitCountryCode, long? exitAsn)
    {
        if (result == null) throw new ArgumentNullException("result");
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
```

Append optional constructor parameters to `CandidateScanResult` so existing call sites still compile:

```csharp
IDictionary<ServiceKind, ServiceObservation> serviceObservations = null,
long? exitAsn = null
```

Copy them into read-only properties:

```csharp
ExitAsn = exitAsn;
ServiceObservations = new ReadOnlyDictionary<ServiceKind, ServiceObservation>(
    new Dictionary<ServiceKind, ServiceObservation>(serviceObservations ??
        new Dictionary<ServiceKind, ServiceObservation>()));
```

Add:

```csharp
public long? ExitAsn { get; private set; }
public IDictionary<ServiceKind, ServiceObservation> ServiceObservations { get; private set; }
```

Keep `ServiceResults` unchanged as the compatibility view.

- [ ] **Step 4: Run the full C# test harness**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: all existing tests and the new observation tests pass.

- [ ] **Step 5: Commit the model boundary**

```powershell
git add -- src\Models.cs tests\Tests.cs
git commit -m "feat: add raw service observation model"
```

### Task 2: Emit observations at the scanner boundary

**Files:**
- Modify: `src/CompatibilityScanner.cs`
- Modify: `src/MonitorWorker.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing scanner tests for completeness and privacy**

Extend `CompatibilityScanning()` with a fixed clock and exit identity containing ASN:

```csharp
DateTime observed = new DateTime(2026, 9, 21, 2, 0, 0, DateTimeKind.Utc);
var observedScan = new CompatibilityScanner(new FakeMihomo(), probe, "probe",
    new FixedExitIdentityProbe(new ExitIdentity(Fingerprint('D'), "SG", "ok", 64510, observed)),
    new FakeClock(observed)).ScanSelected(new CandidateNode("observed", 1),
        new[] { ServiceKind.ChatGPT, ServiceKind.Gemini, ServiceKind.Discord });
Equal(3, observedScan.ServiceObservations.Count, "one observation per selected service");
Equal(observed, observedScan.ServiceObservations[ServiceKind.Discord].ObservedUtc,
    "scanner uses injected clock for observation time");
Equal(64510L, observedScan.ServiceObservations[ServiceKind.ChatGPT].ExitAsn,
    "scanner attaches verified ASN");
string serializedScan = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(observedScan);
Equal(false, serializedScan.Contains("203.0.113."), "serialized scan cannot contain raw IP");
```

Update the test fake to return the supplied `ExitIdentity` without introducing a raw-IP field.

- [ ] **Step 2: Run the build and verify constructor/observation failures**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: compilation fails because the five-argument scanner constructor and extended `ExitIdentity` do not exist, or the observation map is empty.

- [ ] **Step 3: Inject a clock and construct observations only after final probe classification**

Add `private readonly IClock clock;`, preserve both existing constructors, and add:

```csharp
public CompatibilityScanner(IMihomoClient mihomo, IServiceProbe probe, string probeGroup,
    IExitIdentityProbe exitIdentityProbe, IClock clock)
{
    this.mihomo = mihomo;
    this.probe = probe;
    this.probeGroup = probeGroup;
    this.exitIdentityProbe = exitIdentityProbe;
    this.clock = clock ?? new SystemClock();
}
```

Make the existing four-argument constructor delegate to this overload with `new SystemClock()`. After AI region policy has produced the final `measurements` map, create observations at one shared timestamp:

```csharp
DateTime observedUtc = clock.UtcNow;
var observations = measurements.ToDictionary(x => x.Key, x => ServiceObservation.FromProbe(
    x.Key, x.Value, observedUtc, identity.Fingerprint, identity.CountryCode, identity.Asn));
```

Pass `observations` and `identity.Asn` to every success, partial, unknown, and failure `CandidateScanResult`. Extend `Failure` to accept and forward the observation map. In `MonitorWorker`, construct the scanner with its existing injected `clock`.

- [ ] **Step 4: Run the build and scanner regressions**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: all tests pass; each selected service produces exactly one observation, and no serialized scan evidence contains a raw IP.

- [ ] **Step 5: Commit the scanner boundary**

```powershell
git add -- src\CompatibilityScanner.cs src\MonitorWorker.cs tests\Tests.cs
git commit -m "feat: record complete service observations"
```

### Task 3: Add verified ASN enrichment and bounded persistence

**Files:**
- Create: `src/ExitNetworkEvidence.cs`
- Modify: `src/ExitIdentity.cs`
- Modify: `src/Program.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing parser, mismatch, cache, and persistence tests**

Register `ExitNetworkEvidenceBehavior();` in `Tests.Main`. Cover these exact cases in that method:

```csharp
private static void ExitNetworkEvidenceBehavior()
{
    byte[] key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
    string rawIp = "203.0.113.9";
    string expected = ExitIdentityKey.Fingerprint(rawIp, key);
    ExitAsnResolution matched = IpWhoExitAsnParser.Parse(
        "{\"success\":true,\"ip\":\"203.0.113.9\",\"country_code\":\"SG\",\"connection\":{\"asn\":64520}}",
        key, expected, "SG");
    Equal(true, matched.Matched, "IPWho identity matches Cloudflare evidence");
    Equal(64520L, matched.Asn, "IPWho ASN is parsed");
    Equal(false, matched.Detail.Contains(rawIp), "ASN diagnostics omit raw IP");

    Equal(false, IpWhoExitAsnParser.Parse(
        "{\"success\":true,\"ip\":\"203.0.113.10\",\"country_code\":\"SG\",\"connection\":{\"asn\":64520}}",
        key, expected, "SG").Matched, "fingerprint mismatch is rejected");
    Equal(false, IpWhoExitAsnParser.Parse(
        "{\"success\":true,\"ip\":\"203.0.113.9\",\"country_code\":\"US\",\"connection\":{\"asn\":64520}}",
        key, expected, "SG").Matched, "country mismatch is rejected");

    DateTime now = new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc);
    var cache = new ExitNetworkEvidenceCache();
    cache.Remember(expected, "SG", 64520, now, TimeSpan.FromMinutes(60));
    long asn;
    Equal(true, cache.TryGet(expected, "SG", now.AddMinutes(59), out asn),
        "ASN cache is valid before sixty minutes");
    Equal(false, cache.TryGet(expected, "SG", now.AddMinutes(60), out asn),
        "ASN cache expires at sixty minutes");
    Equal(false, cache.TryGet(Fingerprint('E'), "SG", now.AddMinutes(1), out asn),
        "different fingerprint misses cache");

    string root = Path.Combine(Path.GetTempPath(), "ccm-asn-" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "exit-network.state");
    try
    {
        var store = new ExitNetworkEvidenceStore(path);
        store.Save(cache, now.AddMinutes(1));
        string json = File.ReadAllText(path);
        Equal(false, json.Contains(rawIp), "ASN cache never persists raw IP");
        File.WriteAllText(path, "{\"Records\":[{\"ExitFingerprint\":\"bad\",\"CountryCode\":\"S\",\"Asn\":0}]}");
        Equal(0, store.Load(now).Records.Count, "malformed ASN records are rejected");
    }
    finally { DeleteDirectoryEventually(root); }
}
```

Also add a fake resolver test proving timeout/provider failure returns the original known fingerprint/country with `Asn == null`.

- [ ] **Step 2: Run the build and verify evidence types are missing**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: compilation fails on `ExitAsnResolution`, `IpWhoExitAsnParser`, and `ExitNetworkEvidenceCache`.

- [ ] **Step 3: Extend the safe exit identity contract**

Change `ExitIdentity` to use this backward-compatible constructor:

```csharp
public ExitIdentity(string fingerprint, string countryCode, string detail,
    long? asn = null, DateTime? observedUtc = null)
{
    Fingerprint = fingerprint ?? "";
    CountryCode = countryCode ?? "";
    Detail = detail ?? "";
    Asn = asn;
    ObservedUtc = observedUtc ?? DateTime.MinValue;
}

public long? Asn { get; private set; }
public DateTime ObservedUtc { get; private set; }

public ExitIdentity WithAsn(long asn, DateTime observedUtc)
{
    return new ExitIdentity(Fingerprint, CountryCode, Detail, asn, observedUtc);
}
```

Move fingerprint calculation into a reusable safe helper and call it from `ExitIdentityParser.Parse`:

```csharp
public static string Fingerprint(string rawIp, byte[] key)
{
    if (String.IsNullOrWhiteSpace(rawIp) || key == null || key.Length == 0) return "";
    using (var hmac = new HMACSHA256(key))
        return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawIp.Trim())))
            .Replace("-", "");
}
```

No public type may retain a raw-IP property.

- [ ] **Step 4: Implement `src/ExitNetworkEvidence.cs`**

Define these contracts:

```csharp
public sealed class ExitAsnResolution
{
    public bool Matched { get; set; }
    public long Asn { get; set; }
    public string Detail { get; set; }
}

public interface IExitAsnResolver
{
    ExitAsnResolution Resolve(ExitIdentity expected, TimeSpan timeout);
}

public sealed class ExitNetworkEvidenceRecord
{
    public string ExitFingerprint { get; set; }
    public string CountryCode { get; set; }
    public long Asn { get; set; }
    public DateTime ObservedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}
```

`IpWhoExitAsnParser.Parse` must deserialize with `JavaScriptSerializer`, require `success == true`, hash returned `ip` immediately with `ExitIdentityKey.Fingerprint`, require an exact fingerprint match, require uppercase country equality, require a positive `connection.asn`, and return only `Matched`, `Asn`, and a bounded safe detail. It must never copy the returned IP into any object or error text.

`IpWhoExitAsnResolver` must use `https://ipwho.is/` with `HttpClientHandler { Proxy = new WebProxy(proxyUrl), UseProxy = true, UseCookies = false, AllowAutoRedirect = false }`, a cancellation token, a 16 KiB body limit, and the same local fingerprint key. Network, timeout, malformed JSON, HTTP, rate-limit, or mismatch errors return `Matched = false`; they do not throw into service health.

`ExitNetworkEvidenceCache` must expose:

```csharp
public bool TryGet(string fingerprint, string countryCode, DateTime nowUtc, out long asn)
public void Remember(string fingerprint, string countryCode, long asn, DateTime observedUtc, TimeSpan lifetime)
internal static bool IsValidRecord(ExitNetworkEvidenceRecord record, DateTime nowUtc, DateTime latestAllowedUtc)
```

Validation requires a 64-character uppercase hexadecimal fingerprint, a two-letter uppercase country, positive ASN, `ObservedUtc <= nowUtc + 5 minutes`, `ExpiresUtc > nowUtc`, and `ExpiresUtc <= ObservedUtc + 60 minutes`. Deduplicate by fingerprint, keep the newest record, and cap records at 256.

`ExitNetworkEvidenceStore` must expose `Load(DateTime nowUtc)` and `Save(ExitNetworkEvidenceCache cache, DateTime nowUtc)`, use a one-megabyte file limit, a path-derived named mutex, UTF-8 without BOM, a unique temporary file, and atomic `File.Replace`/`File.Move`. Corrupt input is archived with a `.corrupt-*` suffix and returns an empty cache.

- [ ] **Step 5: Compose lazy enrichment into the Cloudflare probe**

Keep the current two-argument `CloudflareExitIdentityProbe` constructor for compatibility. Add `MonitorConfiguration.ExitNetworkEvidencePath`, initialize it to `state\exit-network.state`, add a three-argument production constructor `(proxyUrl, keyPath, evidencePath)`, and update both `Program` call sites to pass the configured path. The production constructor creates an `IpWhoExitAsnResolver` and `ExitNetworkEvidenceStore`. Also add an injectable constructor for tests:

```csharp
public CloudflareExitIdentityProbe(string proxyUrl, string keyPath,
    IExitAsnResolver resolver, ExitNetworkEvidenceStore store, IClock clock)
```

After a known Cloudflare identity is obtained:

1. load the cache at `clock.UtcNow`;
2. return cached ASN when fingerprint and country match;
3. otherwise invoke the resolver with the remaining/maximum supplied timeout;
4. accept only `Matched && Asn > 0`;
5. remember for exactly 60 minutes and save;
6. on every enrichment failure return the original fingerprint/country with unknown ASN.

Do not make ASN availability part of `ExitIdentity.Known` or AI region eligibility.

- [ ] **Step 6: Run evidence tests and inspect persisted JSON**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
rg -n "rawIp|203\.0\.113" src
```

Expected: all tests pass; the search finds only parser-local/test material, never a persisted model field, logger call, or display path.

- [ ] **Step 7: Commit ASN evidence**

```powershell
git add -- src\ExitIdentity.cs src\ExitNetworkEvidence.cs src\Program.cs tests\Tests.cs
git commit -m "feat: verify independent exit network evidence"
```

### Task 4: Require independent exits for public-incident consensus

**Files:**
- Modify: `src/ServiceIncidentPolicy.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Replace name-only consensus tests with independent-exit cases**

Create a test helper that builds definite service failures with observation evidence, then assert:

```csharp
ServiceIncidentConsensus sameExit = ServiceIncidentPolicy.EvaluateConsensus(
    IncidentObservationScan("current", Fingerprint('A'), "SG", 64530, service),
    new[] {
        IncidentObservationScan("node-2", Fingerprint('A'), "SG", 64530, service),
        IncidentObservationScan("node-3", Fingerprint('A'), "SG", 64530, service)
    }, service);
Equal(false, sameExit.Passed, "three names on one exit cannot open incident");
Equal(1, sameExit.DistinctFingerprintCount, "duplicate exits are counted once");

ServiceIncidentConsensus oneAsn = ServiceIncidentPolicy.EvaluateConsensus(
    IncidentObservationScan("current", Fingerprint('A'), "SG", 64530, service),
    new[] {
        IncidentObservationScan("node-2", Fingerprint('B'), "SG", 64530, service),
        IncidentObservationScan("node-3", Fingerprint('C'), "JP", 64530, service)
    }, service);
Equal(false, oneAsn.Passed, "one known ASN cannot establish diversity");

ServiceIncidentConsensus twoAsns = ServiceIncidentPolicy.EvaluateConsensus(
    IncidentObservationScan("current", Fingerprint('A'), "SG", 64530, service),
    new[] {
        IncidentObservationScan("node-2", Fingerprint('B'), "SG", 64531, service),
        IncidentObservationScan("node-3", Fingerprint('C'), "JP", 64531, service)
    }, service);
Equal(true, twoAsns.Passed, "two known ASNs establish independent consensus");

ServiceIncidentConsensus countryFallback = ServiceIncidentPolicy.EvaluateConsensus(
    IncidentObservationScan("current", Fingerprint('A'), "SG", 64530, service),
    new[] {
        IncidentObservationScan("node-2", Fingerprint('B'), "JP", null, service),
        IncidentObservationScan("node-3", Fingerprint('C'), "SG", 64530, service)
    }, service);
Equal(true, countryFallback.Passed, "country fallback applies when ASN is incomplete");

ServiceIncidentConsensus noDiversity = ServiceIncidentPolicy.EvaluateConsensus(
    IncidentObservationScan("current", Fingerprint('A'), "SG", 64530, service),
    new[] {
        IncidentObservationScan("node-2", Fingerprint('B'), "SG", null, service),
        IncidentObservationScan("node-3", Fingerprint('C'), "SG", null, service)
    }, service);
Equal(false, noDiversity.Passed, "incomplete ASN with one country is rejected");
```

Retain tests for mismatched failure kinds, unknown results, duplicate node names, and fewer than two alternatives.

- [ ] **Step 2: Run the build and confirm name-only behavior fails the new assertions**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: compilation fails because `EvaluateConsensus` is missing, or old `HasConsensus` incorrectly accepts shared exits/one ASN.

- [ ] **Step 3: Implement an explainable consensus result**

Add:

```csharp
public sealed class ServiceIncidentConsensus
{
    public bool Passed { get; set; }
    public int DistinctFingerprintCount { get; set; }
    public int DistinctCountryCount { get; set; }
    public int DistinctAsnCount { get; set; }
    public string RejectionReason { get; set; }
}
```

Implement `EvaluateConsensus(current, alternatives, service)` with this order:

1. require a definite current `ServiceObservation` failure;
2. deduplicate alternatives by node name and reject fewer than two;
3. require every considered alternative to have a definite failure with the same `FailureKind`;
4. combine current and alternatives and require at least three distinct nonempty fingerprints;
5. when every observation has `ExitAsn`, require at least two distinct ASNs;
6. otherwise require at least two distinct nonempty country codes;
7. fill counts and a bounded enum-like reason such as `current-not-definite`, `insufficient-alternatives`, `failure-kind-mismatch`, `insufficient-fingerprints`, `insufficient-asn-diversity`, or `insufficient-country-diversity`.

Keep `HasConsensus` as a compatibility wrapper returning the `Passed` property from the same `EvaluateConsensus` arguments. Update `FailureKind` and `TryDefiniteFailure` to prefer `ServiceObservations` and fall back to `ServiceResults` only for legacy test/build compatibility.

- [ ] **Step 4: Run all consensus regressions**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: all tests pass, including rejection of shared fingerprints and a single known ASN.

- [ ] **Step 5: Commit independent consensus**

```powershell
git add -- src\ServiceIncidentPolicy.cs tests\Tests.cs
git commit -m "fix: require independent exits for incident consensus"
```

### Task 5: Separate suppression from node-health history

**Files:**
- Modify: `src/ServiceIncidentPolicy.cs`
- Modify: `src/MonitorWorker.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing suppression and history-projection tests**

Extend service-incident tests with a definite raw failure:

```csharp
CandidateScanResult rawFailure = IncidentObservationScan(
    "current", Fingerprint('A'), "SG", 64530, ServiceKind.ChatGPT);
CandidateScanResult suppressed = ServiceIncidentPolicy.AttachSuppressed(rawFailure,
    new[] { ServiceKind.ChatGPT });
ServiceObservation raw = suppressed.ServiceObservations[ServiceKind.ChatGPT];
Equal(ServiceOutcome.Failure, raw.Outcome, "suppression preserves raw failure");
Equal(ProbeFailureKind.Service, raw.FailureKind, "suppression preserves failure kind");
Equal(false, raw.CountedForNodeHealth, "suppressed failure is excluded from node history");
Equal(rawFailure.ServiceResults[ServiceKind.ChatGPT].Detail, raw.Detail,
    "suppression preserves raw detail");

CandidateScanResult healthView = ServiceIncidentPolicy.ForNodeHealth(suppressed);
Equal(CandidateHealth.Unknown, healthView.Health,
    "only suppressed evidence produces unknown node-health view");
Equal(0, healthView.ServiceObservations.Count(x => x.Value.CountedForNodeHealth),
    "node-health view has no attributable observations");

CandidateScanResult partiallySuppressed = ScanWithObservations(
    ObservationFailure(ServiceKind.ChatGPT, false),
    ObservationSuccess(ServiceKind.GitHub, true));
CandidateScanResult countedView = ServiceIncidentPolicy.ForNodeHealth(partiallySuppressed);
Equal(CandidateHealth.Compatible, countedView.Health,
    "unsuppressed success remains usable for node history");
```

Add a worker orchestration assertion that repeated suppressed current/alternative failures do not change health records, quality sample count, experience success/failure counts, or standby stability memory, while the snapshot still displays the failure.

- [ ] **Step 2: Run the build and verify suppression currently conflates raw and historical state**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: new assertions fail because suppression lacks observation attribution and history consumers use the raw scan directly.

- [ ] **Step 3: Change suppression to clone observations without rewriting evidence**

In `AttachSuppressed`, clone `ServiceObservations`. For a probed suppressed service, replace only that entry with `WithNodeHealthCounting(false)`. For an intentionally unprobed service, add:

```csharp
new ServiceObservation {
    Service = service,
    Outcome = ServiceOutcome.Unknown,
    FailureKind = ProbeFailureKind.Unverified,
    Detail = "服务端点暂时熔断，本轮未探测，不归因于节点",
    ElapsedMilliseconds = 0,
    ObservedUtc = DateTime.UtcNow,
    ExitFingerprint = scan.ExitFingerprint,
    ExitCountryCode = scan.ExitCountryCode,
    ExitAsn = scan.ExitAsn,
    CountedForNodeHealth = false
}
```

Preserve every existing probed `ProbeResult` and raw scan failure. If one or more required services were unprobed and there is no raw definite failure, keep the existing overall `Unknown` presentation behavior.

- [ ] **Step 4: Implement one node-health projection**

Add `ServiceIncidentPolicy.ForNodeHealth(CandidateScanResult scan)`. It must derive health only from observations with `CountedForNodeHealth == true`:

- first attributable `Failure` maps Region to `RegionBlocked`, Transient to `Transient`, and other definite failures to `ServiceFailed`;
- no failure plus any attributable `Unknown` maps to `Unknown`;
- all attributable observations successful maps to `Compatible`;
- zero attributable observations maps to `Unknown`;
- preserve raw observations, exit evidence, timing, and compatibility `ServiceResults` in the returned scan.

Do not use this projection for UI display, incident consensus, recovery validation, rollback validation, or switch authorization.

- [ ] **Step 5: Route every history write through the projection**

In `MonitorWorker`, compute `CandidateScanResult nodeHealthScan = ServiceIncidentPolicy.ForNodeHealth(scan)` immediately before each historical write. Use the projection for:

- assignments to `state.Records` and calls to `state.RememberPreferred`;
- quality-history response, jitter, and success values (calculate a separate history response from the projected scan; do not reuse a suppressed service's raw latency);
- every `experience.Observe` call, including the final pass over `scans.Values`;
- standby/stability memory updates that consume a `CandidateScanResult`.

Skip the write when the projected health is `Unknown`. Continue using the raw scan for `MonitorSnapshot`, `ServiceEvidencePolicy` decisions, full recovery/rollback checks, and service-incident diagnostics. Remove broad `suppressedServices.Count == 0` history gates after the projection makes attribution service-specific.

- [ ] **Step 6: Run C# tests and focused automatic-worker regressions**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: all tests pass; raw failures remain visible, while suppressed observations do not improve or reduce node history.

- [ ] **Step 7: Commit the history boundary**

```powershell
git add -- src\ServiceIncidentPolicy.cs src\MonitorWorker.cs tests\Tests.cs
git commit -m "fix: separate raw failures from node history"
```

### Task 6: Add safe diagnostics and full orchestration coverage

**Files:**
- Modify: `src/MonitorWorker.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write end-to-end incident orchestration tests**

Add scripted worker cases proving all of the following:

```text
current + two alternatives, same fingerprint -> no circuit
three fingerprints, all ASN 64530 -> no circuit
three fingerprints across ASN 64530 and 64531 -> circuit opens
one ASN unknown, countries SG and JP -> circuit opens by country fallback
one ASN unknown, all country SG -> no circuit
circuit active -> current scan still probes every selected service
full recovery validation -> every selected service is probed
rollback validation -> every selected service is probed
```

Each fake exit probe must return only fingerprint, country, ASN, and observation time. Assert that log text does not contain fake raw IPs or complete node names.

- [ ] **Step 2: Run the build and confirm orchestration gaps**

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`.

Expected: at least the consensus logging/orchestration assertions fail before worker integration.

- [ ] **Step 3: Log only safe consensus diagnostics**

Replace the boolean-only worker call with one `ServiceIncidentConsensus consensus` produced from `confirmation`, `incidentChecks`, and `failedService`. Log exactly the safe aggregate fields:

```csharp
logger.Write("service incident consensus service=" + failedService +
    " fingerprints=" + consensus.DistinctFingerprintCount +
    " countries=" + consensus.DistinctCountryCount +
    " asns=" + consensus.DistinctAsnCount +
    " passed=" + consensus.Passed +
    " reason=" + consensus.RejectionReason);
```

Open the ten-minute circuit only when `consensus.Passed`. Keep current-node cycles on `requiredServices`, candidate incident checks on their intended focused service set, and recovery/rollback validation on all `requiredServices`.

- [ ] **Step 4: Run all repository tests**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
git diff --check
```

Expected: every command exits `0`; `git diff --check` prints nothing.

- [ ] **Step 5: Run Serena diagnostics on modified C# files**

Use Serena `get_symbols_overview` for `src/Models.cs`, `src/ExitNetworkEvidence.cs`, `src/ServiceIncidentPolicy.cs`, and `src/MonitorWorker.cs`, then run its diagnostics/type checks on modified C# files if available. Resolve every error and rerun `build.ps1`.

Expected: symbol queries succeed and no C# diagnostics remain.

- [ ] **Step 6: Commit orchestration and diagnostics**

```powershell
git add -- src\MonitorWorker.cs tests\Tests.cs
git commit -m "test: cover independent incident orchestration"
```

### Task 7: Release, install, observe, and archive preview.8

**Files:**
- Modify: `src/Program.cs`
- Modify: `src/AssemblyInfo.cs`
- Modify: `scripts/install.ps1`
- Modify: `package-release.ps1`
- Modify: `tests/Release.Tests.ps1`
- Modify: `README.md`
- Modify: `README.en.md`
- Modify: `QUICKSTART.md`
- Create: `docs/release-notes/v0.7.0-preview.8.md`

- [ ] **Step 1: Add failing release-contract assertions**

Update `Tests.Main` and `tests/Release.Tests.ps1` to require `0.7.0-preview.8`, package directory `ClashCompatibilityMonitor-v0.7.0-preview.8`, its ZIP/SHA-256 names, and release notes describing:

```text
公共服务事故必须来自三个不同实际出口
ASN 完整时至少两个 ASN；ASN 不完整时至少两个国家
原始检测结果与节点健康历史分离
不保存、记录或显示原始出口 IP
```

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1`.

Expected: FAIL against preview.7 metadata.

- [ ] **Step 2: Bump every version source and write release documentation**

Set:

```csharp
public const string Version = "0.7.0-preview.8";
[assembly: AssemblyVersion("0.7.0.0")]
[assembly: AssemblyFileVersion("0.7.0.0")]
[assembly: AssemblyInformationalVersion("0.7.0-preview.8")]
```

Update installer/package names and all release assertions to preview.8. Update the Chinese README, English README, and quick start with the independent-exit rule, raw/history separation, 60-minute ASN cache, and raw-IP privacy guarantee. Create `docs/release-notes/v0.7.0-preview.8.md` with changes, safety behavior, upgrade note, and verification commands.

- [ ] **Step 3: Run final source verification**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
node .\clash\enhancement.test.js
node .\clash\pure-clash.test.js
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1
git diff --check
```

Expected: every command exits `0` and the diff check is empty.

- [ ] **Step 4: Commit the non-overwriting release**

```powershell
git add -- src\Program.cs src\AssemblyInfo.cs scripts\install.ps1 package-release.ps1 tests\Release.Tests.ps1 README.md README.en.md QUICKSTART.md docs\release-notes\v0.7.0-preview.8.md
git commit -m "release: prepare v0.7.0-preview.8"
```

- [ ] **Step 5: Protect Clash-owned files before packaging/installing**

Record SHA-256 hashes for the active Clash configuration and every subscription/provider file discovered from that configuration. Store the hash list in a temporary path outside both the repository and installation directory. Do not edit any Clash-owned file.

- [ ] **Step 6: Package preview.8 and verify the sidecar**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\package-release.ps1
Get-FileHash .\dist\ClashCompatibilityMonitor-v0.7.0-preview.8.zip -Algorithm SHA256
Get-Content .\dist\ClashCompatibilityMonitor-v0.7.0-preview.8.zip.sha256
```

Expected: package succeeds and the computed uppercase SHA-256 equals the sidecar value.

- [ ] **Step 7: Replace the local installation with preview.8**

Close the running preview.7 monitor gracefully, wait for it to exit, then run the packaged `scripts\install.ps1`. Verify:

```text
exactly one ClashCompatibilityMonitor process is running
the running executable path is C:\Users\lenovo\AppData\Local\ClashCompatibilityMonitor\ClashCompatibilityMonitor.exe
file ProductVersion/InformationalVersion reports 0.7.0-preview.8
the Startup shortcut resolves to the preview.8 launcher/install path
```

If graceful shutdown fails, stop only the verified process whose executable path is inside the named installation directory; do not terminate Clash/Mihomo.

- [ ] **Step 8: Observe two automatic cycles and one real or controlled failure**

Without clicking “立即复检”, wait for at least two scheduled log entries. Record their UTC/local timestamps, selected node, required-service list, and state-machine decision. Then observe a naturally occurring definite failure or use the existing reversible test/fake path to exercise failure handling; do not disrupt the user's live subscription or rewrite Clash configuration.

Verify that a shared-exit failure does not open a circuit, an independent consensus does, and any automatic switch still passes the existing state-machine authorization and rollback safeguards. If no safe real failure occurs, report the automated orchestration test as the controlled evidence instead of fabricating a live switch.

- [ ] **Step 9: Recheck UI and protected files**

Open the details window and verify ChatGPT and Gemini both appear, every selected service is present, raw failures remain visible during suppression, and there is no horizontal service-table scrollbar at the normal window size/DPI. Recompute protected Clash hashes and require an exact match with Step 5.

- [ ] **Step 10: Copy immutable artifacts to the product directory**

Copy the preview.8 release folder, ZIP, and SHA-256 sidecar to:

```text
D:\CodexStudyDocs\成品（最终文件）\长期通用（跨学期）\工具\ClashCompatibilityMonitor
```

Do not overwrite or republish preview.7. Verify the copied ZIP hash against the copied sidecar.

- [ ] **Step 11: Final repository and runtime report**

Run `git status --short` and require a clean worktree. Report the final commit, installed version, one running PID/path, current node, the two automatic-cycle times, live/controlled failure result, UI result, protected-file hash result, product paths, and final SHA-256.
