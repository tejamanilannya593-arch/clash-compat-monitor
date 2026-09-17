# Official-Region Low-Latency Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Select the lowest-latency verified node only after its actual exit is inside the official ChatGPT and Gemini web-app region intersection.

**Architecture:** Keep Mihomo's per-proxy delay endpoint as the fast all-node prefilter, then use a small persistent actual-exit cache to reject known unsupported exits before HTTP probing. Validate at most three intersection-eligible candidates with two-second critical-service probes, rank them by real-service P75, and fully validate only the winner and fallbacks as needed.

**Tech Stack:** C#/.NET Framework 4, WinForms, Mihomo named-pipe REST API, `JavaScriptSerializer`, PowerShell packaging and installation scripts.

---

### Task 1: Add the official AI region-intersection policy

**Files:**
- Create: `src/AiRegionPolicy.cs`
- Modify: `src/ExitIdentity.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing region-policy tests**

Add these assertions to the exit-identity policy tests in `tests/Tests.cs`:

```csharp
Equal(true, AiRegionPolicy.SupportsBoth("JP"), "Japan is in the ChatGPT and Gemini intersection");
Equal(true, AiRegionPolicy.SupportsBoth("SG"), "Singapore is in the ChatGPT and Gemini intersection");
Equal(true, AiRegionPolicy.SupportsBoth("TW"), "Taiwan is in the ChatGPT and Gemini intersection");
Equal(false, AiRegionPolicy.SupportsBoth("HK"), "Hong Kong is excluded because ChatGPT does not officially support it");
Equal(false, AiRegionPolicy.SupportsBoth("CN"), "mainland China is excluded from the shared consumer-web intersection");
Equal(false, AiRegionPolicy.SupportsBoth(""), "unknown exit is never assumed supported");
Equal("2026-09-17", AiRegionPolicy.SnapshotDate, "region policy exposes its dated snapshot");
```

- [ ] **Step 2: Run the test suite and verify RED**

Run:

```powershell
& .\build.ps1
```

Expected: compilation fails because `AiRegionPolicy` does not exist.

- [ ] **Step 3: Implement the policy**

Create `src/AiRegionPolicy.cs` with two explicit ISO-3166 code sets and an intersection check:

```csharp
using System;
using System.Collections.Generic;

public static class AiRegionPolicy
{
    public const string SnapshotDate = "2026-09-17";

    private static readonly HashSet<string> ChatGpt = new HashSet<string>(
        ChatGptSupportedRegions.AllCodes, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> GeminiWeb = new HashSet<string>(
        ("AX AL DZ AS AD AO AI AQ AG AR AM AW AU AT AZ BH BD BB BE BZ BJ BM BT BO BA BW BR IO VG BN BG BF BI " +
         "CV KH CM CA BQ KY CF TD CL CN CX CC CO KM CK CR CI HR CW CZ CD DK DJ DM DO EC EG SV GQ ER EE SZ ET FK " +
         "FO FJ FI FR GF PF TF GA GE DE GH GI GR GL GD GP GU GT GG GN GW GY HT HM HN HK HU IS IN ID IQ IE IM IL " +
         "IT JM JP JE JO KZ KE KI XK KW KG LA LV LB LS LR LY LI LT LU MO MG MW MY MV ML MT MH MQ MR MU YT MX FM " +
         "MD MC MN ME MS MA MZ MM NA NR NP NL NC NZ NI NE NG NU NF MK MP NO OM PK PW PS PA PG PY PE PH PN PL PT " +
         "PR QA CY CG RE RO RW BL SH KN LC MF PM VC WS SM ST SA SN RS SC SL SG SX SK SI SB SO ZA GS KR SS ES LK " +
         "SD SR SJ SE CH TW TJ TZ TH BS GM TL TG TK TO TT TN TR TM TC TV VI UG UA AE GB US UM UY UZ VU VA VE VN " +
         "WF EH YE ZM ZW").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.OrdinalIgnoreCase);

    public static bool SupportsBoth(string countryCode)
    {
        if (String.IsNullOrWhiteSpace(countryCode)) return false;
        string code = countryCode.Trim();
        return ChatGpt.Contains(code) && GeminiWeb.Contains(code) && !String.Equals(code, "CN", StringComparison.OrdinalIgnoreCase);
    }
}
```

Expose the existing list from `src/ExitIdentity.cs` without duplicating it:

```csharp
public static IEnumerable<string> AllCodes { get { return Codes; } }
```

- [ ] **Step 4: Run tests and verify GREEN**

Run `& .\build.ps1`.

Expected: all tests pass, including the seven new region-policy assertions.

- [ ] **Step 5: Commit the policy**

```powershell
git add src/AiRegionPolicy.cs src/ExitIdentity.cs tests/Tests.cs
git commit -m "feat: require official AI region intersection"
```

### Task 2: Persist a bounded actual-exit eligibility cache

**Files:**
- Create: `src/RegionEligibilityCache.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing cache tests**

Add a temporary-file test that verifies a fresh matching record is accepted and every invalidation dimension is enforced:

```csharp
DateTime regionNow = new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc);
var regionCache = new RegionEligibilityCache();
regionCache.Remember("scope-a", "node-a", "exit-a", "JP", regionNow);
string cachedCountry;
bool cachedSupported;
Equal(true, regionCache.TryGet("scope-a", "node-a", regionNow, out cachedCountry, out cachedSupported), "fresh exit is cached");
Equal(true, cachedSupported, "fresh supported exit remains eligible");
Equal("JP", cachedCountry, "cached country is returned");
Equal(false, regionCache.TryGet("scope-b", "node-a", regionNow, out cachedCountry, out cachedSupported), "subscription scope change invalidates region cache");
Equal(false, regionCache.TryGet("scope-a", "node-a", regionNow.AddHours(24), out cachedCountry, out cachedSupported), "region cache expires at 24 hours");
regionCache.Remember("scope-a", "node-hk", "exit-hk", "HK", regionNow);
Equal(true, regionCache.TryGet("scope-a", "node-hk", regionNow, out cachedCountry, out cachedSupported), "unsupported exit result is cached");
Equal(false, cachedSupported, "unsupported cached exit remains ineligible");
```

Add a round-trip assertion using `RegionEligibilityStore.Save` and `Load`, and assert the saved file contains neither raw IP addresses nor more than 256 entries.

- [ ] **Step 2: Run the test suite and verify RED**

Run `& .\build.ps1`.

Expected: compilation fails because `RegionEligibilityCache` and `RegionEligibilityStore` do not exist.

- [ ] **Step 3: Implement cache records and lookup rules**

Create `src/RegionEligibilityCache.cs` with these public shapes:

```csharp
public sealed class RegionEligibilityRecord
{
    public string Scope { get; set; }
    public string Node { get; set; }
    public string ExitFingerprint { get; set; }
    public string CountryCode { get; set; }
    public string PolicyVersion { get; set; }
    public DateTime CheckedUtc { get; set; }
}

public sealed class RegionEligibilityCache
{
    public RegionEligibilityCache() { Records = new List<RegionEligibilityRecord>(); }
    public List<RegionEligibilityRecord> Records { get; set; }

    public bool TryGet(string scope, string node, DateTime now, out string countryCode, out bool supported)
    {
        RegionEligibilityRecord item = Records
            .Where(x => x.Scope == scope && x.Node == node && x.PolicyVersion == AiRegionPolicy.SnapshotDate &&
                        x.CheckedUtc > now.AddHours(-24))
            .OrderByDescending(x => x.CheckedUtc).FirstOrDefault();
        countryCode = item == null ? "" : item.CountryCode;
        supported = item != null && AiRegionPolicy.SupportsBoth(item.CountryCode);
        return item != null;
    }

    public void Remember(string scope, string node, string exitFingerprint, string countryCode, DateTime now)
    {
        Records.RemoveAll(x => x.Scope == scope && x.Node == node);
        Records.Add(new RegionEligibilityRecord {
            Scope = scope, Node = node, ExitFingerprint = exitFingerprint,
            CountryCode = countryCode, PolicyVersion = AiRegionPolicy.SnapshotDate, CheckedUtc = now
        });
        Records = Records.OrderByDescending(x => x.CheckedUtc).Take(256).ToList();
    }
}
```

Implement `RegionEligibilityStore` using the repository's atomic `.tmp` then `File.Replace`/`File.Move` pattern:

```csharp
public sealed class RegionEligibilityStore
{
    private readonly string path;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();
    public RegionEligibilityStore(string path) { this.path = path; }

    public RegionEligibilityCache Load()
    {
        if (!File.Exists(path)) return new RegionEligibilityCache();
        try
        {
            RegionEligibilityCache cache = json.Deserialize<RegionEligibilityCache>(File.ReadAllText(path));
            if (cache == null || cache.Records == null) throw new InvalidDataException();
            cache.Records = cache.Records.Where(x => x != null)
                .OrderByDescending(x => x.CheckedUtc).Take(256).ToList();
            return cache;
        }
        catch
        {
            try { File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch { }
            return new RegionEligibilityCache();
        }
    }

    public void Save(RegionEligibilityCache cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        cache.Records = (cache.Records ?? new List<RegionEligibilityRecord>())
            .Where(x => x != null).OrderByDescending(x => x.CheckedUtc).Take(256).ToList();
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json.Serialize(cache), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
}
```

The record model deliberately stores only the hashed exit fingerprint, never a raw exit IP address.

- [ ] **Step 4: Run tests and verify GREEN**

Run `& .\build.ps1`.

Expected: all cache lifetime, policy-version, scope, unsupported-region, size-bound, privacy, and round-trip tests pass.

- [ ] **Step 5: Commit the cache**

```powershell
git add src/RegionEligibilityCache.cs tests/Tests.cs
git commit -m "feat: cache actual exit eligibility"
```

### Task 3: Make scanning enforce the shared official-region gate

**Files:**
- Modify: `src/CompatibilityScanner.cs`
- Modify: `src/StatusReport.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing scanner tests**

Extend the existing `FakeExitIdentityProbe` scanner tests:

```csharp
CandidateScanResult hkAi = new CompatibilityScanner(mihomo, passingProbe, "probe",
    new FakeExitIdentityProbe(new ExitIdentity("fp-hk", "HK", "ok")))
    .ScanSelected(new CandidateNode("香港名字无关", 1), new[] { ServiceKind.ChatGPT, ServiceKind.Gemini });
Equal(CandidateHealth.RegionBlocked, hkAi.Health, "actual Hong Kong exit cannot carry both AI services");

CandidateScanResult jpAi = new CompatibilityScanner(mihomo, passingProbe, "probe",
    new FakeExitIdentityProbe(new ExitIdentity("fp-jp", "JP", "ok")))
    .ScanSelected(new CandidateNode("香港名称但日本出口", 1), new[] { ServiceKind.ChatGPT, ServiceKind.Gemini });
Equal(CandidateHealth.Compatible, jpAi.Health, "actual supported exit overrides provider label");

string regionStatus = StatusReport.Format(DateTime.UtcNow, "node", CandidateHealth.Compatible,
    null, "current healthy", "ok");
Equal(true, regionStatus.Contains("AI 地区规则：ChatGPT ∩ Gemini 官方支持地区（快照 2026-09-17）"),
    "status exposes the official region snapshot");
```

- [ ] **Step 2: Run tests and verify RED**

Run `& .\build.ps1`.

Expected: the Hong Kong intersection assertion fails because only the ChatGPT-specific rule is currently applied.

- [ ] **Step 3: Apply one shared region decision to both AI services**

In `CompatibilityScanner.ScanSelected`, after the identity task completes and before classifying individual results, calculate:

```csharp
bool aiSelected = services.Contains(ServiceKind.ChatGPT) || services.Contains(ServiceKind.Gemini);
bool knownAiExit = !aiSelected || identity.Known;
bool supportedAiExit = !aiSelected || (identity.Known && AiRegionPolicy.SupportsBoth(identity.CountryCode));
```

For both ChatGPT and Gemini successful/partial probe results, replace them with:

```csharp
if (!knownAiExit)
    result = ProbeResult.Unverified("无法确认实际出口，不能验证 AI 官方支持地区", result.ElapsedMilliseconds);
else if (!supportedAiExit)
    result = ProbeResult.RegionFailure("实际出口不在 ChatGPT 与 Gemini 官方支持地区交集中", result.ElapsedMilliseconds);
```

Remove the narrower ChatGPT-only condition.

Add this line to `StatusReport.Format` immediately before the final conversation-verification disclaimer:

```csharp
"AI 地区规则：ChatGPT ∩ Gemini 官方支持地区（快照 " + AiRegionPolicy.SnapshotDate + "）" + Environment.NewLine +
```

- [ ] **Step 4: Run tests and verify GREEN**

Run `& .\build.ps1`.

Expected: all scanner and region tests pass.

- [ ] **Step 5: Commit scanner enforcement**

```powershell
git add src/CompatibilityScanner.cs src/StatusReport.cs tests/Tests.cs
git commit -m "fix: gate AI candidates by actual supported exit"
```

### Task 4: Restore a bounded fast-selection path

**Files:**
- Modify: `src/StartupRecovery.cs`
- Modify: `src/MonitorWorker.cs`
- Modify: `tests/Tests.cs`

- [ ] **Step 1: Write failing selection-policy tests**

Add policy tests proving live delay order wins, only three eligible candidates are compared, and real-service P75 outranks synthetic delay:

```csharp
string[] delayOrder = StartupRecovery.RankFastCandidates(
    new[] { new CandidateNode("slow", 1), new CandidateNode("fast", 1), new CandidateNode("middle", 1) },
    new Dictionary<string, int> { { "slow", 500 }, { "fast", 90 }, { "middle", 180 } }, "current", 3);
Equal("fast,middle,slow", String.Join(",", delayOrder), "all-node delay prefilter is ordered by live Mihomo latency");
Equal(true, StartupRecovery.ShouldStopAfterEligibleCandidates(3), "comparison stops after three intersection-eligible candidates");
Equal(false, StartupRecovery.ShouldStopAfterEligibleCandidates(2), "comparison keeps looking until three eligible candidates exist");
Equal("实测优质节点", StartupRecovery.FastSelectionSummary(799), "sub-800 ms target is labeled preferred");
Equal("当前合格候选中延迟最低，但未达到 800 ms 优质标准",
    StartupRecovery.FastSelectionSummary(801), "slowest winner is not mislabeled as preferred");
```

Keep the existing `RankVerifiedFastTargets` test where a node with slightly higher Mihomo delay wins because its real service P75 is lower.

- [ ] **Step 2: Run tests and verify RED**

Run `& .\build.ps1`.

Expected: compilation fails because `ShouldStopAfterEligibleCandidates` does not exist.

- [ ] **Step 3: Add the three-candidate stop policy**

Add to `StartupRecovery`:

```csharp
public static bool ShouldStopAfterEligibleCandidates(int eligibleCandidates)
{
    return eligibleCandidates >= 3;
}

public static string FastSelectionSummary(double responseMilliseconds)
{
    return responseMilliseconds <= 800 ? "实测优质节点" :
        "当前合格候选中延迟最低，但未达到 800 ms 优质标准";
}
```

- [ ] **Step 4: Integrate delay, cache, region, and service ranking**

In the current failure branch of `MonitorWorker.RunOnce`:

1. Keep the existing concurrent `GetDelay(..., 2500)` tasks across every real candidate.
2. Build `liveRanked` directly from those results; do not prepend standby or historical candidates.
3. Load `RegionEligibilityCache` from `Path.Combine(config.RootPath, "state", "region-eligibility.json")`.
4. Iterate `liveRanked` in order. Skip a fresh cached unsupported record using:

```csharp
string cachedCountry;
bool cachedSupported;
if (regionCache.TryGet(memoryScope, candidate.Name, clock.UtcNow, out cachedCountry, out cachedSupported) &&
    !cachedSupported) continue;
```

For every remaining candidate, call:

```csharp
CandidateScanResult quickScan = scanner.ScanSelected(candidate, fastServices, TimeSpan.FromSeconds(2));
regionCache.Remember(memoryScope, candidate.Name, quickScan.ExitFingerprint,
    quickScan.ExitCountryCode, clock.UtcNow);
```

5. Add only scans with `AiRegionPolicy.SupportsBoth(quickScan.ExitCountryCode)` and `CanFastFailoverTarget` to `quickScans`.
6. Stop as soon as three eligible scans exist. If the delay-ordered candidates are exhausted, rank one or two eligible scans rather than selecting an unknown exit.
7. Use `RankVerifiedFastTargets` to rank by real-service P75, then perform the existing full `servicesToProbe` validation on the best candidate. If it fails, validate the next ranked candidate without repeating all-node delay tests.
8. Save the bounded region cache atomically at the end of the cycle.
9. Build the user-visible switch decision with `FastSelectionSummary`; never label a result above 800 ms as an “优质节点”. Keep the existing 30-second post-switch observation interval and rollback behavior.

Log only safe identifiers and timing totals:

```csharp
logger.Write("fast selection delay_ms=" + delayElapsed + " checked=" + checkedCandidates +
    " eligible=" + quickScans.Count + " selected_response_ms=" + selectedResponse);
```

- [ ] **Step 5: Run tests and verify GREEN**

Run:

```powershell
& .\build.ps1
node .\clash\enhancement.test.js
```

Expected: all C# tests and enhancement tests pass.

- [ ] **Step 6: Commit fast selection**

```powershell
git add src/StartupRecovery.cs src/MonitorWorker.cs tests/Tests.cs
git commit -m "fix: restore fast region-safe node selection"
```

### Task 5: Publish immutable preview.5 artifacts and documentation

**Files:**
- Modify: `src/Program.cs`
- Modify: `src/AssemblyInfo.cs`
- Modify: `scripts/install.ps1`
- Modify: `package-release.ps1`
- Modify: `tests/Release.Tests.ps1`
- Modify: `README.md`
- Modify: `README.en.md`
- Modify: `QUICKSTART.md`
- Create: `docs/release-notes/v0.7.0-preview.5.md`

- [ ] **Step 1: Write failing release checks**

Change release tests to require `0.7.0-preview.5`, the new release note, and these phrases in current documentation:

```powershell
if (!$currentDocs.Contains('ChatGPT 与 Gemini 官方支持地区的交集')) { throw 'Official region intersection is undocumented.' }
if (!$currentDocs.Contains('延迟最低的三个合格候选')) { throw 'Fast candidate limit is undocumented.' }
```

- [ ] **Step 2: Run release tests and verify RED**

Run `& .\tests\Release.Tests.ps1`.

Expected: failure because version strings, release notes, and documentation still reference preview.4.

- [ ] **Step 3: Update version and user documentation**

Set product version to `0.7.0-preview.5` while retaining Windows file version `0.7.0.0`. Document that node labels are ignored, actual exit countries are checked against the dated official intersection, all-node delay tests are concurrent, and only the three lowest-latency eligible nodes receive quick service validation.

The release note must state:

```markdown
- 先按实际出口判断 ChatGPT 与 Gemini 官方支持地区交集。
- 全节点 Mihomo 延迟并发初筛，仅复检延迟最低的三个合格候选。
- 最终按关键服务真实 P75 延迟选择，不再把第一个可用节点称为最优。
- 不修改 Clash 配置文件，只更新现有代理组选择。
```

- [ ] **Step 4: Run the complete release verification**

Run:

```powershell
& .\package-release.ps1
```

Expected: C# tests, enhancement tests, release checks, archive generation, and SHA-256 sidecar generation all succeed.

- [ ] **Step 5: Commit release metadata**

```powershell
git add src/Program.cs src/AssemblyInfo.cs scripts/install.ps1 package-release.ps1 tests/Release.Tests.ps1 README.md README.en.md QUICKSTART.md docs/release-notes/v0.7.0-preview.5.md
git commit -m "release: publish region-safe preview.5"
```

### Task 6: Install and verify the real machine path

**Files:**
- Runtime install: `%LOCALAPPDATA%\ClashCompatibilityMonitor`
- Final artifacts: `D:\CodexStudyDocs\成品（最终文件）\长期通用（跨学期）\工具\ClashCompatibilityMonitor`

- [ ] **Step 1: Install the freshly built executable**

Run:

```powershell
& .\scripts\install.ps1 -SourceExe '.\bin\ClashCompatibilityMonitor.exe'
```

Expected JSON: version `0.7.0-preview.5`, exactly one process, `ClashFilesUnchanged=true`, and `LegacyBrowserCompanionRemoved=true`.

- [ ] **Step 2: Observe one real selection cycle**

Read `current-status.txt` and the new `fast selection` log entry. Verify:

- all-node delay time is recorded;
- no more than three supported candidates are fully compared;
- the selected actual exit is inside `AiRegionPolicy` intersection;
- Google, GitHub, ChatGPT entrance, and Gemini entrance each respond within 2000 ms or the program keeps searching;
- no raw exit IP is written to logs or status.

- [ ] **Step 3: Verify installation boundaries**

Compare pre/post SHA-256 for Clash `profiles.yaml` and `clash-verge.yaml`, verify one monitor process, and verify the Startup shortcut targets the installed executable.

- [ ] **Step 4: Copy immutable artifacts and verify hashes**

Copy the preview.5 folder, ZIP, and `.sha256` sidecar to the D-drive final directory. Recompute SHA-256 at the destination and assert it matches the sidecar.

- [ ] **Step 5: Record final evidence**

Report the installed version, selected node, actual exit country, four measured service latencies, total selection time, final ZIP path, and SHA-256. State explicitly that HTTP entrance reachability is not equivalent to a logged-in ChatGPT or Gemini conversation.
