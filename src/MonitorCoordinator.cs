using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

public interface IMonitorCycleRunner
{
    MonitorSnapshot Run(UserPreferences preferences);
}

public enum MonitorCycleTrigger { Startup, Scheduled, Requested }

public interface ITriggeredCycleRunner : IMonitorCycleRunner
{
    MonitorSnapshot Run(UserPreferences preferences, MonitorCycleTrigger trigger);
}

public interface IRestorableCycleRunner : IMonitorCycleRunner
{
    bool RestorePrevious();
}

public interface IProgressCycleRunner : IMonitorCycleRunner
{
    event Action<MonitorSnapshot> Progress;
    Func<bool> ShouldStop { get; set; }
}

public interface IAccountVerificationRunner : IMonitorCycleRunner
{
    bool RecordBrowserConversationProof(string node, string exitFingerprint,
        ServiceKind service, DateTime verifiedUtc, int protocolVersion);
    void ReportServiceFailure(string node, ServiceKind service, DateTime reportedUtc);
    void ReportBrowserConversationFailure(string node, string exitFingerprint, ServiceKind service,
        BrowserVerificationOutcome outcome, bool messageSent, DateTime reportedUtc);
}

public sealed class MonitorCoordinator : IDisposable
{
    private readonly IMonitorCycleRunner runner;
    private readonly TimeSpan interval;
    private readonly TimeSpan watchdog;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly AutoResetEvent browserWake = new AutoResetEvent(false);
    private readonly object snapshotGate = new object();
    private readonly object preferencesGate = new object();
    private readonly object accountCommandGate = new object();
    private readonly object browserStatusGate = new object();
    private readonly List<Action<IAccountVerificationRunner>> accountCommands = new List<Action<IAccountVerificationRunner>>();
    private readonly BrowserConversationCoordinator browserCoordinator;
    private readonly IClock clock;
    private readonly TimeSpan browserPollWait;
    private Thread thread;
    private MonitorSnapshot latest;
    private UserPreferences preferences = UserPreferences.Defaults();
    private string lastAttentionKey;
    private int checkRequested = 1;
    private int paused;
    private int restoreRequested;
    private int stopping;
    private int cancelCycle;
    private readonly bool persistStatistics;
    private BrowserVerificationStatus browserStatus = BrowserVerificationStatus.None;
    private string browserStatusDetail = "";
    private string browserName = "";
    private string browserExtensionVersion = "";

    public MonitorCoordinator(IMonitorCycleRunner runner, TimeSpan interval, TimeSpan watchdog,
        bool persistStatistics = false, IClock clock = null, IChallengeSource challengeSource = null,
        TimeSpan? browserPollWait = null)
    {
        if (runner == null) throw new ArgumentNullException("runner");
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("interval");
        if (watchdog <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("watchdog");
        this.runner = runner;
        this.interval = interval;
        this.watchdog = watchdog;
        this.persistStatistics = persistStatistics;
        this.clock = clock ?? new SystemClock();
        this.browserPollWait = browserPollWait ?? TimeSpan.FromSeconds(25);
        if (this.browserPollWait <= TimeSpan.Zero || this.browserPollWait > TimeSpan.FromSeconds(25))
            throw new ArgumentOutOfRangeException("browserPollWait");
        browserCoordinator = new BrowserConversationCoordinator(this.clock,
            challengeSource ?? new CryptographicChallengeSource());
        latest = MonitorSnapshot.CreateState(MonitorRunState.Starting, "正在启动", DateTime.MinValue, DateTime.MaxValue);
        var progressive = runner as IProgressCycleRunner;
        if (progressive != null)
        {
            progressive.ShouldStop = () => Volatile.Read(ref stopping) != 0 || Volatile.Read(ref paused) != 0 || Volatile.Read(ref cancelCycle) != 0;
            progressive.Progress += OnProgress;
        }
    }

    public event Action<MonitorSnapshot> SnapshotChanged;
    public event Action<MonitorSnapshot> AttentionRequired;

    public MonitorSnapshot Latest
    {
        get { lock (snapshotGate) return latest; }
    }

    public bool IsBrowserCompanionOnline
    {
        get { return browserCoordinator.IsCompanionOnline(clock.UtcNow); }
    }

    public string BrowserCompanionDescription
    {
        get
        {
            lock (browserStatusGate)
                return String.IsNullOrWhiteSpace(browserName) ? "未连接" :
                    browserName + " 浏览器伴侣 " + browserExtensionVersion +
                    (IsBrowserCompanionOnline ? " 已连接" : " 已离线");
        }
    }

    public void Start()
    {
        if (thread != null) return;
        thread = new Thread(Loop) { IsBackground = true, Name = "Clash monitor coordinator" };
        thread.Start();
    }

    public void RequestCheck()
    {
        Interlocked.Exchange(ref checkRequested, 1);
        wake.Set();
    }

    public void RequestRestorePrevious()
    {
        Interlocked.Exchange(ref restoreRequested, 1);
        RequestCheck();
    }

    public void ReportServiceFailure(string node, ServiceKind service, DateTime reportedUtc)
    {
        lock (accountCommandGate)
            accountCommands.Add(value => value.ReportServiceFailure(node, service, reportedUtc));
        RequestCheck();
    }

    public BrowserVerificationStart StartBrowserVerification(bool userInitiated)
    {
        MonitorSnapshot snapshot = Latest;
        BrowserVerificationStart started = browserCoordinator.StartCurrent(snapshot,
            EligibleAiServices(snapshot), userInitiated);
        if (started == BrowserVerificationStart.Started)
        {
            PublishBrowserStatus(BrowserVerificationStatus.Running, "正在进行真实对话验证");
            browserWake.Set();
        }
        else if (started == BrowserVerificationStart.CompanionOffline)
            PublishBrowserStatus(BrowserVerificationStatus.CompanionOffline, "浏览器扩展未连接");
        return started;
    }

    public BrowserBridgeMessage HandleBrowserMessage(BrowserBridgeMessage request)
    {
        if (!BrowserMessageValidator.IsValidRequest(request))
            return BrowserResponse("error", request == null ? null : request.RequestId, "invalid-request");
        DateTime now = clock.UtcNow;
        if (String.Equals(request.Type, "hello", StringComparison.Ordinal))
        {
            browserCoordinator.ObserveCompanion(request.Browser, now);
            lock (browserStatusGate)
            {
                browserName = request.Browser;
                browserExtensionVersion = request.ExtensionVersion;
            }
            PublishBrowserStatus(BrowserVerificationStatus.Ready,
                request.Browser + " 浏览器伴侣 " + request.ExtensionVersion + " 已连接");
            TryStartAutomaticBrowserVerification(Latest);
            return BrowserResponse("ready", request.RequestId, null);
        }
        if (String.Equals(request.Type, "poll", StringComparison.Ordinal))
        {
            browserCoordinator.ObserveCompanion(request.Browser, now);
            BrowserVerificationTask task = browserCoordinator.Poll(request.Browser, now);
            if (task == null && Volatile.Read(ref stopping) == 0)
            {
                browserWake.WaitOne(browserPollWait);
                now = clock.UtcNow;
                task = browserCoordinator.Poll(request.Browser, now);
            }
            return task == null ? BrowserResponse("idle", request.RequestId, null) : BrowserTaskResponse(request, task);
        }
        if (String.Equals(request.Type, "cancel", StringComparison.Ordinal))
        {
            bool cancelled = browserCoordinator.Cancel(request.TaskId);
            if (cancelled) PublishBrowserStatus(BrowserVerificationStatus.Cancelled, "浏览器验证已取消");
            browserWake.Set();
            return BrowserResponse(cancelled ? "ack" : "error", request.RequestId,
                cancelled ? null : "unknown-task");
        }

        BrowserVerificationOutcome outcome;
        ServiceKind service;
        if (!Enum.TryParse(request.Outcome, false, out outcome) ||
            !Enum.TryParse(request.Service, false, out service))
            return BrowserResponse("error", request.RequestId, "invalid-result");
        var result = new BrowserVerificationResult {
            TaskId = request.TaskId,
            Service = service,
            Challenge = request.Challenge,
            Outcome = outcome,
            MessageSent = request.MessageSent,
            ElapsedMilliseconds = request.ElapsedMilliseconds
        };
        MonitorSnapshot current = Latest;
        BrowserVerificationAcceptance acceptance = browserCoordinator.Accept(result, current, now);
        if (acceptance != BrowserVerificationAcceptance.Accepted)
        {
            if (acceptance == BrowserVerificationAcceptance.Drifted)
                PublishBrowserStatus(BrowserVerificationStatus.Cancelled, "节点或出口已变化，请重新验证");
            browserWake.Set();
            return BrowserResponse("error", request.RequestId,
                acceptance.ToString().ToLowerInvariant());
        }

        PublishBrowserOutcome(result);
        if (result.Outcome == BrowserVerificationOutcome.Passed)
            QueueBrowserProof(current.ActualNode, current.ExitFingerprint, result.Service, now);
        else if (BrowserConversationCoordinator.IsRollbackEligible(result))
            QueueBrowserFailure(current.ActualNode, current.ExitFingerprint, result.Service, result.Outcome,
                result.MessageSent, now);
        browserWake.Set();
        return BrowserResponse("ack", request.RequestId, null);
    }

    public void SetPaused(bool value)
    {
        Interlocked.Exchange(ref paused, value ? 1 : 0);
        if (value) Interlocked.Exchange(ref cancelCycle, 1);
        if (value) Publish(Latest.WithState(MonitorRunState.Paused, "自动优化已暂停", DateTime.MaxValue), false);
        else RequestCheck();
        wake.Set();
    }

    public void UpdatePreferences(UserPreferences value)
    {
        if (value == null || value.RequiredServices == null || value.RequiredServices.Count == 0)
            throw new ArgumentException("At least one required service is needed.", "value");
        lock (preferencesGate) preferences = Copy(value);
        browserCoordinator.SetConsent(value.BrowserConversationVerification);
        RequestCheck();
    }

    public void RevokeBrowserVerificationConsent()
    {
        browserCoordinator.SetConsent(false);
        browserCoordinator.CancelAll();
        PublishBrowserStatus(BrowserVerificationStatus.Cancelled, "已撤销浏览器自动实测授权");
        browserWake.Set();
    }

    private void Loop()
    {
        DateTime nextRunUtc = DateTime.UtcNow;
        bool firstCycle = true;
        while (Volatile.Read(ref stopping) == 0)
        {
            if (Volatile.Read(ref paused) != 0)
            {
                try { RunAccountCommands(); }
                catch (Exception ex)
                {
                    Publish(Latest.WithState(MonitorRunState.Degraded,
                        "浏览器验证记录失败：" + ex.Message, DateTime.MaxValue), true);
                }
                wake.WaitOne(TimeSpan.FromSeconds(30));
                continue;
            }

            TimeSpan remaining = nextRunUtc - DateTime.UtcNow;
            if (Volatile.Read(ref checkRequested) == 0 && remaining > TimeSpan.Zero)
            {
                wake.WaitOne(remaining);
                continue;
            }

            bool explicitlyRequested = Interlocked.Exchange(ref checkRequested, 0) != 0;
            MonitorCycleTrigger trigger = firstCycle ? MonitorCycleTrigger.Startup :
                explicitlyRequested ? MonitorCycleTrigger.Requested : MonitorCycleTrigger.Scheduled;
            firstCycle = false;
            bool restore = Interlocked.Exchange(ref restoreRequested, 0) != 0;
            Interlocked.Exchange(ref cancelCycle, 0);
            UserPreferences current;
            lock (preferencesGate) current = Copy(preferences);
            Publish(Latest.WithState(MonitorRunState.Checking, "正在检测当前节点及所选服务", DateTime.MaxValue), false);
            Task<MonitorSnapshot> cycle = Task.Factory.StartNew(() => {
                RunAccountCommands();
                if (restore)
                {
                    var restorable = runner as IRestorableCycleRunner;
                    if (restorable == null || !restorable.RestorePrevious())
                        return Latest.WithState(MonitorRunState.Degraded, "上一个节点未通过复检或不存在，保留当前连接", DateTime.UtcNow.Add(interval));
                }
                var triggered = runner as ITriggeredCycleRunner;
                return triggered == null ? runner.Run(current) : triggered.Run(current, trigger);
            },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool completed = false;
            while (Volatile.Read(ref stopping) == 0 && elapsed.Elapsed < watchdog)
                if (WaitWithoutThrowing(cycle, TimeSpan.FromMilliseconds(50))) { completed = true; break; }
            if (!completed)
            {
                Interlocked.Exchange(ref cancelCycle, 1);
                var stuck = Latest.WithState(MonitorRunState.Stuck, "检测超时，正在等待当前操作退出", DateTime.MaxValue);
                Publish(stuck, true);
                while (Volatile.Read(ref stopping) == 0 && !WaitWithoutThrowing(cycle, TimeSpan.FromMilliseconds(100))) { }
            }
            if (Volatile.Read(ref stopping) != 0) break;
            if (Volatile.Read(ref paused) != 0) { if (cycle.IsFaulted) { var observed = cycle.Exception; } continue; }

            if (cycle.IsFaulted)
            {
                Exception cause = cycle.Exception == null ? null : cycle.Exception.GetBaseException();
                var degraded = Latest.WithState(MonitorRunState.Degraded,
                    cause == null ? "检测失败" : "检测失败：" + cause.Message,
                    DateTime.UtcNow.Add(interval));
                Publish(degraded, true);
            }
            else if (cycle.IsCompleted && cycle.Result != null)
            {
                Publish(cycle.Result, cycle.Result.State == MonitorRunState.Degraded || cycle.Result.State == MonitorRunState.Stuck);
                TryStartAutomaticBrowserVerification(cycle.Result);
            }
            RunStatistics.CycleCompleted(Latest.State, elapsed.Elapsed.TotalSeconds);
            if (persistStatistics) RunStatistics.SaveLocal();
            DateTime requested = Latest.NextCheckUtc;
            nextRunUtc = requested > DateTime.UtcNow && requested < DateTime.UtcNow.AddMinutes(5) ? requested : DateTime.UtcNow.Add(interval);
        }
        Publish(MonitorSnapshot.CreateState(MonitorRunState.Stopped, "已退出", DateTime.UtcNow, DateTime.MaxValue), false);
    }

    private void RunAccountCommands()
    {
        List<Action<IAccountVerificationRunner>> pending;
        lock (accountCommandGate)
        {
            pending = new List<Action<IAccountVerificationRunner>>(accountCommands);
            accountCommands.Clear();
        }
        IAccountVerificationRunner accountRunner = runner as IAccountVerificationRunner;
        if (accountRunner == null) return;
        foreach (Action<IAccountVerificationRunner> command in pending) command(accountRunner);
    }

    private void OnProgress(MonitorSnapshot value)
    {
        if (Volatile.Read(ref stopping) == 0 && Volatile.Read(ref paused) == 0 && Volatile.Read(ref cancelCycle) == 0)
        {
            if (String.IsNullOrEmpty(value.ActualNode)) value = Latest.WithState(value.State, value.Decision, value.NextCheckUtc);
            Publish(value, false);
        }
    }

    private void Publish(MonitorSnapshot value, bool attention)
    {
        lock (browserStatusGate) value = value.WithBrowserStatus(browserStatus, browserStatusDetail);
        bool emitAttention = false;
        lock (snapshotGate)
        {
            latest = value;
            if (attention)
            {
                string key = value.State + "|" + value.Decision;
                emitAttention = !String.Equals(lastAttentionKey, key, StringComparison.Ordinal);
                lastAttentionKey = key;
            }
            else if (value.State == MonitorRunState.Running) lastAttentionKey = null;
        }
        Action<MonitorSnapshot> changed = SnapshotChanged;
        if (changed != null) changed(value);
        if (emitAttention)
        {
            Action<MonitorSnapshot> required = AttentionRequired;
            if (required != null) required(value);
        }
    }

    private void TryStartAutomaticBrowserVerification(MonitorSnapshot snapshot)
    {
        UserPreferences current;
        lock (preferencesGate) current = Copy(preferences);
        if (!current.BrowserConversationVerification || snapshot == null) return;
        BrowserVerificationStart started = browserCoordinator.StartCurrent(snapshot,
            EligibleAiServices(snapshot), false);
        if (started == BrowserVerificationStart.Started)
        {
            PublishBrowserStatus(BrowserVerificationStatus.Running, "正在进行真实对话验证");
            browserWake.Set();
        }
    }

    private IEnumerable<ServiceKind> EligibleAiServices(MonitorSnapshot snapshot)
    {
        UserPreferences current;
        lock (preferencesGate) current = Copy(preferences);
        if (snapshot == null || snapshot.Services == null) return new ServiceKind[0];
        var available = new HashSet<ServiceKind>(snapshot.Services.Where(x =>
            (x.Service == ServiceKind.ChatGPT || x.Service == ServiceKind.Gemini) &&
            x.Available && x.Evidence == ProbeFailureKind.None && !x.AccountVerified).Select(x => x.Service));
        return current.RequiredServices.Where(available.Contains).ToList();
    }

    private void QueueBrowserProof(string node, string exitFingerprint, ServiceKind service, DateTime verifiedUtc)
    {
        lock (accountCommandGate)
            accountCommands.Add(value => {
                if (!value.RecordBrowserConversationProof(node, exitFingerprint, service, verifiedUtc,
                    BrowserConversationProof.CurrentProtocolVersion))
                    throw new InvalidOperationException("节点、出口或登录链路已经变化，请重新验证");
            });
        RequestCheck();
    }

    private void QueueBrowserFailure(string node, string exitFingerprint, ServiceKind service,
        BrowserVerificationOutcome outcome, bool messageSent, DateTime reportedUtc)
    {
        lock (accountCommandGate)
            accountCommands.Add(value => value.ReportBrowserConversationFailure(node, exitFingerprint, service,
                outcome, messageSent, reportedUtc));
        RequestCheck();
    }

    private void PublishBrowserOutcome(BrowserVerificationResult result)
    {
        BrowserVerificationStatus status;
        string detail;
        switch (result.Outcome)
        {
            case BrowserVerificationOutcome.Passed:
                status = BrowserVerificationStatus.Passed; detail = "真实对话已通过，正在复检网络链路"; break;
            case BrowserVerificationOutcome.SignInRequired:
                status = BrowserVerificationStatus.SignInRequired; detail = "需要先在浏览器中登录"; break;
            case BrowserVerificationOutcome.ChallengeRequired:
                status = BrowserVerificationStatus.ChallengeRequired; detail = "网页要求完成验证码或安全挑战"; break;
            case BrowserVerificationOutcome.AutomationUnsupported:
                status = BrowserVerificationStatus.AutomationUnsupported; detail = "网页结构暂不受支持"; break;
            case BrowserVerificationOutcome.ConversationError:
                status = BrowserVerificationStatus.ConversationError; detail = "消息已发送，但服务返回明确错误"; break;
            case BrowserVerificationOutcome.GenerationTimeout:
                status = BrowserVerificationStatus.GenerationTimeout; detail = "消息已发送，但等待回复超时"; break;
            default:
                status = BrowserVerificationStatus.Cancelled; detail = "浏览器验证已取消"; break;
        }
        PublishBrowserStatus(status, detail);
    }

    private void PublishBrowserStatus(BrowserVerificationStatus status, string detail)
    {
        MonitorSnapshot value;
        lock (browserStatusGate)
        {
            browserStatus = status;
            browserStatusDetail = detail ?? "";
            lock (snapshotGate)
            {
                value = latest.WithBrowserStatus(status, browserStatusDetail);
                latest = value;
            }
        }
        Action<MonitorSnapshot> changed = SnapshotChanged;
        if (changed != null) changed(value);
    }

    private static BrowserBridgeMessage BrowserTaskResponse(BrowserBridgeMessage request,
        BrowserVerificationTask task)
    {
        return new BrowserBridgeMessage {
            Type = "run",
            ProtocolVersion = BrowserNativeProtocol.ProtocolVersion,
            RequestId = request.RequestId,
            TaskId = task.TaskId,
            Service = task.Service.ToString(),
            Node = task.Node,
            Challenge = task.Challenge,
            Url = task.Service == ServiceKind.ChatGPT ? "https://chatgpt.com/" : "https://gemini.google.com/app",
            Prompt = "Reply with exactly this token and nothing else: " + task.Challenge,
            ExpiresUtc = task.ExpiresUtc.ToUniversalTime().ToString("o")
        };
    }

    private static BrowserBridgeMessage BrowserResponse(string type, string requestId, string error)
    {
        return new BrowserBridgeMessage {
            Type = type,
            ProtocolVersion = BrowserNativeProtocol.ProtocolVersion,
            RequestId = requestId,
            Error = error
        };
    }

    private static bool WaitWithoutThrowing(Task task, TimeSpan timeout)
    {
        try { return task.Wait(timeout); }
        catch (AggregateException) { return true; }
    }

    private static UserPreferences Copy(UserPreferences value)
    {
        return new UserPreferences {
            FirstRunComplete = value.FirstRunComplete,
            AutomaticOptimization = value.AutomaticOptimization,
            BrowserConversationVerification = value.BrowserConversationVerification,
            RequiredServices = new System.Collections.Generic.List<ServiceKind>(value.RequiredServices)
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        wake.Set();
        browserWake.Set();
        var progressive = runner as IProgressCycleRunner;
        if (progressive != null) progressive.Progress -= OnProgress;
        if (thread == null || thread.Join(TimeSpan.FromSeconds(2)))
        {
            wake.Dispose();
            browserWake.Dispose();
        }
    }
}
