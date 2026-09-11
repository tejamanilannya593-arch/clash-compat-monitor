using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

public interface IMonitorCycleRunner
{
    MonitorSnapshot Run(UserPreferences preferences);
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
    bool RecordAccountVerification(string node, string exitFingerprint,
        IEnumerable<ServiceKind> services, DateTime verifiedUtc);
    void ReportServiceFailure(string node, ServiceKind service, DateTime reportedUtc);
}

public sealed class AccountVerificationSession
{
    internal AccountVerificationSession(MonitorSnapshot snapshot, bool resumeWhenFinished)
    {
        Snapshot = snapshot;
        ResumeWhenFinished = resumeWhenFinished;
    }
    internal MonitorSnapshot Snapshot { get; private set; }
    internal bool ResumeWhenFinished { get; private set; }
    public string Node { get { return Snapshot == null ? "" : Snapshot.ActualNode; } }
    public string ExitFingerprint { get { return Snapshot == null ? "" : Snapshot.ExitFingerprint; } }
}

public sealed class MonitorCoordinator : IDisposable
{
    private readonly IMonitorCycleRunner runner;
    private readonly TimeSpan interval;
    private readonly TimeSpan watchdog;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly object snapshotGate = new object();
    private readonly object preferencesGate = new object();
    private readonly object accountCommandGate = new object();
    private readonly List<Action<IAccountVerificationRunner>> accountCommands = new List<Action<IAccountVerificationRunner>>();
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

    public MonitorCoordinator(IMonitorCycleRunner runner, TimeSpan interval, TimeSpan watchdog, bool persistStatistics = false)
    {
        if (runner == null) throw new ArgumentNullException("runner");
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("interval");
        if (watchdog <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("watchdog");
        this.runner = runner;
        this.interval = interval;
        this.watchdog = watchdog;
        this.persistStatistics = persistStatistics;
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

    public void RequestAccountVerification(string node, string exitFingerprint,
        IEnumerable<ServiceKind> services, DateTime verifiedUtc)
    {
        var selected = new List<ServiceKind>(services ?? new ServiceKind[0]);
        lock (accountCommandGate)
            accountCommands.Add(value => {
                if (!value.RecordAccountVerification(node, exitFingerprint, selected, verifiedUtc))
                    throw new InvalidOperationException("节点、出口或登录链路已经变化，请重新验证");
            });
        RequestCheck();
    }

    public void ReportServiceFailure(string node, ServiceKind service, DateTime reportedUtc)
    {
        lock (accountCommandGate)
            accountCommands.Add(value => value.ReportServiceFailure(node, service, reportedUtc));
        RequestCheck();
    }

    public AccountVerificationSession BeginAccountVerification()
    {
        bool wasPaused = Volatile.Read(ref paused) != 0;
        SetPaused(true);
        return new AccountVerificationSession(Latest, !wasPaused);
    }

    public void CompleteAccountVerification(AccountVerificationSession session,
        IEnumerable<ServiceKind> verifiedServices, ServiceKind? failedService, DateTime reportedUtc)
    {
        if (session == null) throw new ArgumentNullException("session");
        var selected = new List<ServiceKind>(verifiedServices ?? new ServiceKind[0]);
        lock (accountCommandGate)
        {
            if (selected.Count > 0)
                accountCommands.Add(value => {
                    if (!value.RecordAccountVerification(session.Node, session.ExitFingerprint, selected, reportedUtc))
                        throw new InvalidOperationException("节点、出口或登录链路已经变化，请重新验证");
                });
            if (failedService.HasValue)
                accountCommands.Add(value => value.ReportServiceFailure(session.Node,
                    failedService.Value, reportedUtc));
        }
        FinishAccountVerification(session);
    }

    public void CancelAccountVerification(AccountVerificationSession session)
    {
        if (session == null) return;
        FinishAccountVerification(session);
    }

    private void FinishAccountVerification(AccountVerificationSession session)
    {
        if (session.ResumeWhenFinished) SetPaused(false);
        else wake.Set();
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
        RequestCheck();
    }

    private void Loop()
    {
        DateTime nextRunUtc = DateTime.UtcNow;
        while (Volatile.Read(ref stopping) == 0)
        {
            if (Volatile.Read(ref paused) != 0)
            {
                try { RunAccountCommands(); }
                catch (Exception ex)
                {
                    Publish(Latest.WithState(MonitorRunState.Degraded,
                        "账号验证记录失败：" + ex.Message, DateTime.MaxValue), true);
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

            Interlocked.Exchange(ref checkRequested, 0);
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
                return runner.Run(current);
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
        var progressive = runner as IProgressCycleRunner;
        if (progressive != null) progressive.Progress -= OnProgress;
        if (thread == null || thread.Join(TimeSpan.FromSeconds(2))) wake.Dispose();
    }
}
