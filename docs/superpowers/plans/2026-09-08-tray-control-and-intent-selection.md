# Tray Control and Intent Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build v0.2.0 as a low-memory tray application that learns the user's service requirements once, automatically recommends and applies a stable compatible node, exposes per-service measurements on demand, wakes an existing instance when launched again, and cannot hang forever on Mihomo pipe I/O.

**Architecture:** Keep `MonitorWorker` as the single decision engine on one background thread. Add bounded pipe I/O, persisted user preferences, immutable UI snapshots, a coordinator for serialized commands, a native Win32 tray host, and a lazily created WinForms details window. Clash remains the source of node inventory and continues to display the actual selected leaf node.

**Tech Stack:** C#/.NET Framework 4.x, Win32 Shell notification API through P/Invoke, WinForms loaded only for the details window, named pipes for Mihomo and single-instance activation, existing PowerShell build and release scripts.

---

## File structure

- Create `src/UserPreferences.cs`: selected services, defaults, validation, and atomic persistence.
- Create `src/MonitorSnapshot.cs`: immutable status and per-service result objects consumed by UI.
- Create `src/MonitorCoordinator.cs`: one background loop, command coalescing, pause/resume, snapshot publication, and watchdog state.
- Create `src/InstanceActivation.cs`: mutex plus named-event activation protocol.
- Create `src/TrayHost.cs`: native notification-area icon, menu commands, one-shot notifications, and UI-thread dispatch.
- Create `src/DetailsForm.cs`: first-run settings and on-demand status/details UI.
- Modify `src/MihomoPipeClient.cs`: bounded connect/write/read behavior.
- Modify `src/Models.cs`: retain per-service probe results in a scan result.
- Modify `src/CompatibilityScanner.cs`: scan caller-selected required services and return measurements.
- Modify `src/MonitorWorker.cs`: accept preferences, return a snapshot, and preserve existing stability gates.
- Modify `src/Program.cs`: compose activation, coordinator, tray, and lazy details window.
- Modify `build.ps1`: reference `System.Drawing.dll` and `System.Windows.Forms.dll`.
- Modify `tests/Tests.cs`: deterministic tests for each non-visual unit.
- Modify `README.md`, `tests/Release.Tests.ps1`, and `package-release.ps1`: document and package v0.2.0.

### Task 1: Persist explicit service requirements

**Files:**
- Create: `src/UserPreferences.cs`
- Modify: `src/Program.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Write failing preference tests**

Add a `UserPreferenceBehavior()` test and call it from `Tests.Main`:

```csharp
string root = Path.Combine(Path.GetTempPath(), "monitor-prefs-" + Guid.NewGuid().ToString("N"));
string path = Path.Combine(root, "preferences.state");
var store = new UserPreferenceStore(path);
UserPreferences defaults = store.Load();
Equal(true, defaults.RequiredServices.Contains(ServiceKind.ChatGPT), "default includes ChatGPT");
Equal(true, defaults.RequiredServices.Contains(ServiceKind.Gemini), "default includes Gemini");
Equal(true, defaults.RequiredServices.Contains(ServiceKind.Google), "default includes Google");
Equal(true, defaults.RequiredServices.Contains(ServiceKind.GitHub), "default includes GitHub");
Equal(true, defaults.RequiredServices.Contains(ServiceKind.SteamStore), "default includes Steam");
defaults.FirstRunComplete = true;
defaults.RequiredServices = new List<ServiceKind> { ServiceKind.ChatGPT, ServiceKind.GitHub };
store.Save(defaults);
UserPreferences loaded = store.Load();
Equal(true, loaded.FirstRunComplete, "first run persisted");
Equal(2, loaded.RequiredServices.Count, "service selection persisted");
File.WriteAllText(path, "broken", Encoding.UTF8);
Equal(true, store.Load().RequiredServices.Contains(ServiceKind.Gemini), "corrupt preferences use safe defaults");
```

- [ ] **Step 2: Run the tests and verify the new types are missing**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: compilation fails because `UserPreferenceStore` and `UserPreferences` do not exist.

- [ ] **Step 3: Implement validated atomic preferences**

Create these public contracts in `src/UserPreferences.cs`; serialize one `key=value` record per line and write through `<path>.tmp` followed by `File.Replace` when the destination exists or `File.Move` otherwise:

```csharp
public sealed class UserPreferences
{
    public bool FirstRunComplete { get; set; }
    public bool AutomaticOptimization { get; set; }
    public List<ServiceKind> RequiredServices { get; set; }
    public static UserPreferences Defaults()
    {
        return new UserPreferences {
            AutomaticOptimization = true,
            RequiredServices = new List<ServiceKind> {
                ServiceKind.Google, ServiceKind.GitHub, ServiceKind.ChatGPT,
                ServiceKind.Gemini, ServiceKind.SteamStore,
                ServiceKind.SteamCommunity, ServiceKind.SteamApi
            }
        };
    }
}

public sealed class UserPreferenceStore
{
    private readonly string path;
    public UserPreferenceStore(string path) { this.path = path; }
    public UserPreferences Load()
    {
        if (!File.Exists(path)) return UserPreferences.Defaults();
        try
        {
            var values = File.ReadAllLines(path).Select(x => x.Split(new[] { '=' }, 2))
                .Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
            bool firstRun;
            bool automatic;
            if (!Boolean.TryParse(values["firstRun"], out firstRun) ||
                !Boolean.TryParse(values["automatic"], out automatic)) throw new InvalidDataException();
            var services = values["services"].Split(',').Where(x => x.Length > 0)
                .Select(x => (ServiceKind)Enum.Parse(typeof(ServiceKind), x, false)).Distinct().ToList();
            if (services.Count == 0) throw new InvalidDataException();
            return new UserPreferences { FirstRunComplete = firstRun,
                AutomaticOptimization = automatic, RequiredServices = services };
        }
        catch
        {
            string archive = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(path, archive);
            return UserPreferences.Defaults();
        }
    }
    public void Save(UserPreferences value)
    {
        if (value == null || value.RequiredServices == null || value.RequiredServices.Count == 0)
            throw new ArgumentException("At least one service is required.", "value");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp";
        string text = "version=1\r\nfirstRun=" + value.FirstRunComplete +
            "\r\nautomatic=" + value.AutomaticOptimization + "\r\nservices=" +
            String.Join(",", value.RequiredServices.Distinct().Select(x => x.ToString())) + "\r\n";
        File.WriteAllText(temporary, text, Encoding.UTF8);
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }
}
```

Add `PreferencesPath = Path.Combine(root, "state", "preferences.state")` to `MonitorConfiguration`.

- [ ] **Step 4: Run tests and commit**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: all tests pass and the WinExe builds.

Commit: `git add src/UserPreferences.cs src/Program.cs tests/Tests.cs && git commit -m "feat: persist service requirements"`

### Task 2: Bound every Mihomo pipe operation

**Files:**
- Create: `src/BoundedPipeIo.cs`
- Modify: `src/MihomoPipeClient.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Write failing bounded-read tests**

Add a stream that never completes until disposed and verify `BoundedPipeIo.ReadAll` exits:

```csharp
var blocked = new BlockingDisposableStream();
Stopwatch timer = Stopwatch.StartNew();
Throws<TimeoutException>(() => BoundedPipeIo.ReadAll(blocked, TimeSpan.FromMilliseconds(80), 1024), "pipe read timeout");
Equal(true, timer.ElapsedMilliseconds < 1000, "pipe timeout is bounded");
Equal(true, blocked.Disposed, "timeout disposes blocked stream");
Throws<InvalidDataException>(() => BoundedPipeIo.ReadAll(new MemoryStream(new byte[2048]), TimeSpan.FromSeconds(1), 1024), "pipe response cap");
```

`BlockingDisposableStream.Read` waits on a `ManualResetEventSlim`; its `Dispose(bool)` sets `Disposed = true` and releases the event.

- [ ] **Step 2: Run tests and verify failure**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: compilation fails because `BoundedPipeIo` does not exist.

- [ ] **Step 3: Implement bounded I/O and use it from the client**

Implement `BoundedPipeIo.ReadAll(Stream stream, TimeSpan timeout, int maximumBytes)` with one long-running `Task<byte[]>`, `Task.Wait(timeout)`, disposal on timeout, and a final bounded wait before throwing `TimeoutException`. Reject responses beyond 1 MiB. Change `Request` to use `PipeOptions.Asynchronous`, a 3-second connect limit, a 3-second write limit, and a 10-second read limit:

```csharp
using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
{
    pipe.Connect(3000);
    BoundedPipeIo.WriteAll(pipe, headerBytes, bodyBytes, TimeSpan.FromSeconds(3));
    byte[] bytes = BoundedPipeIo.ReadAll(pipe, TimeSpan.FromSeconds(10), 1024 * 1024);
    return PipeHttpCodec.Decode(bytes);
}
```

Preserve `TimeoutException` for callers that already classify timeouts. Do not restart or reconfigure Mihomo on timeout.

- [ ] **Step 4: Run tests and commit**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: timeout and response-cap tests pass with the existing suite.

Commit: `git add src/BoundedPipeIo.cs src/MihomoPipeClient.cs tests/Tests.cs && git commit -m "fix: bound mihomo pipe io"`

### Task 3: Return per-service evidence and a stable UI snapshot

**Files:**
- Create: `src/MonitorSnapshot.cs`
- Modify: `src/Models.cs`
- Modify: `src/CompatibilityScanner.cs`
- Modify: `src/MonitorWorker.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Write failing snapshot and selected-service tests**

```csharp
var probe = new FakeProbe { DefaultResult = ProbeResult.Success(75) };
var scanner = new CompatibilityScanner(new FakeMihomo(), probe, "probe");
CandidateScanResult selected = scanner.Scan(new CandidateNode("node", 1),
    new[] { ServiceKind.ChatGPT, ServiceKind.GitHub });
Equal(2, probe.Calls.Count, "only selected services probed");
Equal(75L, selected.ServiceResults[ServiceKind.ChatGPT].ElapsedMilliseconds, "service latency retained");
var snapshot = MonitorSnapshot.CreateRunning("node", selected, 82.5, "保持当前节点", now, now.AddMinutes(1));
Equal("node", snapshot.ActualNode, "snapshot leaf node");
Equal(2, snapshot.Services.Count, "snapshot retains service evidence");
```

- [ ] **Step 2: Run tests and verify the API mismatch**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: compilation fails because `ServiceResults` and `MonitorSnapshot` do not exist.

- [ ] **Step 3: Implement immutable evidence objects**

Add `IReadOnlyDictionary<ServiceKind, ProbeResult> ServiceResults` to `CandidateScanResult`, copied into a new dictionary in its constructor. Change `CompatibilityScanner.Scan` so its second argument is the complete required-service sequence and it probes each distinct requested service in deterministic enum order; remove process-name inference from the scan path.

Create:

```csharp
public enum MonitorRunState { Starting, Running, Paused, Degraded, Stuck, Stopped }

public sealed class ServiceMeasurement
{
    public ServiceMeasurement(ServiceKind service, bool available, long milliseconds, string detail)
    {
        Service = service;
        Available = available;
        Milliseconds = milliseconds;
        Detail = detail ?? "";
    }
    public ServiceKind Service { get; private set; }
    public bool Available { get; private set; }
    public long Milliseconds { get; private set; }
    public string Detail { get; private set; }
}

public sealed class MonitorSnapshot
{
    public MonitorRunState State { get; private set; }
    public string ActualNode { get; private set; }
    public double? Score { get; private set; }
    public string Decision { get; private set; }
    public DateTime CheckedUtc { get; private set; }
    public DateTime NextCheckUtc { get; private set; }
    public IList<ServiceMeasurement> Services { get; private set; }
    public static MonitorSnapshot CreateRunning(string node, CandidateScanResult scan, double? score,
        string decision, DateTime checkedUtc, DateTime nextCheckUtc)
    {
        return new MonitorSnapshot {
            State = MonitorRunState.Running,
            ActualNode = node ?? "",
            Score = score,
            Decision = decision ?? "",
            CheckedUtc = checkedUtc,
            NextCheckUtc = nextCheckUtc,
            Services = scan.ServiceResults.OrderBy(x => x.Key).Select(x =>
                new ServiceMeasurement(x.Key, x.Value.Passed, x.Value.ElapsedMilliseconds, x.Value.Detail))
                .ToList().AsReadOnly()
        };
    }
}
```

Change `MonitorWorker.RunOnce` to accept `UserPreferences`, use `RequiredServices`, and return the completed `MonitorSnapshot` after writing the existing text status. Retain the 20% improvement, 10-minute hold, fresh-verification, and reload-recovery rules unchanged.

- [ ] **Step 4: Run tests and commit**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: existing compatibility tests are updated to pass explicit default services and the full suite passes.

Commit: `git add src/MonitorSnapshot.cs src/Models.cs src/CompatibilityScanner.cs src/MonitorWorker.cs tests/Tests.cs && git commit -m "feat: publish per-service monitor snapshots"`

### Task 4: Coordinate background work and coalesce commands

**Files:**
- Create: `src/MonitorCoordinator.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Write failing coordinator tests with a fake cycle runner**

```csharp
var runner = new BlockingCycleRunner();
using (var coordinator = new MonitorCoordinator(runner, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(200)))
{
    coordinator.Start();
    runner.WaitUntilEntered();
    coordinator.RequestCheck();
    coordinator.RequestCheck();
    runner.Release();
    runner.WaitForRunCount(2);
    Equal(2, runner.RunCount, "duplicate checks coalesced");
    coordinator.SetPaused(true);
    coordinator.RequestCheck();
    Thread.Sleep(80);
    Equal(2, runner.RunCount, "paused coordinator does not scan");
}
```

Add a watchdog test whose fake cycle blocks beyond the watchdog limit and verify a `Stuck` snapshot is published while command handling remains responsive.

- [ ] **Step 2: Run tests and verify missing coordinator types**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: compilation fails for `MonitorCoordinator` and `IMonitorCycleRunner`.

- [ ] **Step 3: Implement one serialized loop**

Define `IMonitorCycleRunner.Run(UserPreferences)` and adapt `MonitorWorker`. `MonitorCoordinator` owns one background thread, `AutoResetEvent wake`, `CancellationTokenSource stop`, integer flags for `checkRequested` and `paused`, and a thread-safe latest snapshot. `RequestCheck` uses `Interlocked.Exchange(ref checkRequested, 1)`; the loop clears that flag once before each run. Publish `SnapshotChanged` after state transitions, and raise `AttentionRequired` only when entering a new degraded or stuck reason.

- [ ] **Step 4: Run tests and commit**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: serialized execution, coalescing, pause, stop, and notification-deduplication tests pass.

Commit: `git add src/MonitorCoordinator.cs src/MonitorWorker.cs tests/Tests.cs && git commit -m "feat: coordinate background monitoring"`

### Task 5: Make repeated launches show the existing instance

**Files:**
- Create: `src/InstanceActivation.cs`
- Modify: `src/Program.cs`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Write a failing activation test**

```csharp
string id = "ClashCompatibilityMonitor.Test." + Guid.NewGuid().ToString("N");
using (var first = InstanceActivation.TryOwn(id))
{
    Equal(true, first.IsOwner, "first activation owns instance");
    bool signaled = false;
    first.Activated += delegate { signaled = true; };
    first.StartListening();
    using (var second = InstanceActivation.TryOwn(id)) Equal(false, second.IsOwner, "second activation signals owner");
    SpinWait.SpinUntil(() => signaled, 1000);
    Equal(true, signaled, "existing instance activated");
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: compilation fails because `InstanceActivation` does not exist.

- [ ] **Step 3: Implement mutex plus named event**

`TryOwn(id)` opens `<id>.Activate`, then attempts `<id>.Mutex`. A non-owner calls `Set()` on the event and returns with `IsOwner == false`. The owner listens on a background wait registration and raises `Activated`; disposal unregisters the wait and disposes both handles. `Program.Main` returns success for the signaling instance instead of exit code 2.

- [ ] **Step 4: Run tests and commit**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: activation test and existing command-line tests pass.

Commit: `git add src/InstanceActivation.cs src/Program.cs tests/Tests.cs && git commit -m "feat: wake existing monitor instance"`

### Task 6: Add the native tray and lazy details window

**Files:**
- Create: `src/TrayHost.cs`
- Create: `src/DetailsForm.cs`
- Modify: `src/Program.cs`
- Modify: `build.ps1`
- Test: `tests/Tests.cs`

- [ ] **Step 1: Extract and test presentation mapping before creating controls**

Add `MonitorPresentation` to `MonitorSnapshot.cs` and test its Chinese labels:

```csharp
var view = MonitorPresentation.From(snapshot);
Equal("运行正常", view.StateText, "running label");
Equal("新加坡 03｜1.0×", view.NodeText, "leaf node shown");
Equal("可用 · 91 ms", MonitorPresentation.ServiceText(true, 91, "ok"), "service latency label");
Equal("不可用", MonitorPresentation.ServiceText(false, 91, "blocked"), "failure does not advertise latency");
```

- [ ] **Step 2: Run tests and verify presentation types are missing**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: compilation fails for `MonitorPresentation`.

- [ ] **Step 3: Implement the native tray host**

`TrayHost` owns one hidden Win32 message window and registers `Shell_NotifyIcon` with a bundled application icon. Handle double-click and menu IDs for open details, immediate check, pause/resume, restore previous node, and exit. Marshal coordinator events to the message window with `PostMessage`. Balloon notifications are emitted only for `AttentionRequired` and successful node switches; normal cycles remain silent.

- [ ] **Step 4: Implement the lazily created WinForms details window**

`DetailsForm` receives delegates for saving preferences and issuing commands. Its first-run panel contains checked service rows and one “保存并开始自动优化” button. Its status panel contains state, actual node, decision, next check, a bounded service measurement list, and buttons for immediate check, pause/resume, restore, and exit. `FormClosing` cancels closing and hides for ordinary window-close actions; explicit disposal from `TrayHost` releases it on application exit. Do not add charts, animations, WebView, timers faster than one second, or a full node list.

- [ ] **Step 5: Wire the application and build references**

Add `/reference:System.Drawing.dll` and `/reference:System.Windows.Forms.dll` in `build.ps1`. In normal mode, `Program.Main` acquires `InstanceActivation`, initializes Mihomo, preferences, worker and coordinator, then runs `TrayHost.Run()`. Keep `--self-test`, `--once`, and `--dry-run` headless and free of UI initialization.

- [ ] **Step 6: Run automated and manual smoke tests**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`

Expected: all tests pass and the WinExe builds.

Run one built instance, then a second. Expected: exactly one process remains and the first instance shows details. Close details and verify the process remains. Use the tray menu to pause, resume, request a check, and exit.

- [ ] **Step 7: Commit**

Commit: `git add src/TrayHost.cs src/DetailsForm.cs src/MonitorSnapshot.cs src/Program.cs build.ps1 tests/Tests.cs && git commit -m "feat: add low-memory tray controls"`

### Task 7: Release v0.2.0 and verify resource and routing safety

**Files:**
- Modify: `README.md`
- Modify: `package-release.ps1`
- Modify: `tests/Release.Tests.ps1`
- Modify: `src/Program.cs`
- Modify: `src/StatusReport.cs`

- [ ] **Step 1: Make release checks require the new version and behavior**

Update release assertions to require `Version = "0.2.0"`, package folder `ClashCompatibilityMonitor-v0.2.0`, documentation for tray behavior, explicit service preferences, second-launch activation, and bounded Mihomo I/O.

- [ ] **Step 2: Run release checks and verify failure on v0.1.1**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Release.Tests.ps1`

Expected: fails because source and package scripts still identify v0.1.1.

- [ ] **Step 3: Update release metadata and user documentation**

Set `MonitorIdentity.Version` and status output to `0.2.0`; update package paths and README. Document that service times are HTTP probe response times, Steam downloads and games remain governed by direct rules, normal operation is silent, and closing the details window does not stop the monitor.

- [ ] **Step 4: Run the full release pipeline**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File .\package-release.ps1`

Expected: C# tests, enhancement tests, and release checks pass; `dist\ClashCompatibilityMonitor-v0.2.0.zip` is produced with a SHA-256 hash.

- [ ] **Step 5: Perform installed-system verification without changing subscriptions**

Record the active Clash configuration hash and proxy count, install the new binary, and verify: one monitor process; Clash group and UI detail show the same actual leaf node; selected services have fresh measurements; a deliberately unresponsive test pipe times out; no Clash restart or reload occurs; node count and configuration hash remain unchanged.

Measure process working set after five idle minutes with details closed and again after opening/closing details. Expected: tray-only working set is approximately 15 MiB with zero sustained CPU; any retained increase is recorded before release rather than hidden.

- [ ] **Step 6: Commit release changes**

Commit: `git add README.md package-release.ps1 tests/Release.Tests.ps1 src/Program.cs src/StatusReport.cs && git commit -m "release: prepare v0.2.0"`
