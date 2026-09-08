using System;
using System.Threading;
using System.Threading.Tasks;

public interface IMonitorCycleRunner
{
    MonitorSnapshot Run(UserPreferences preferences);
}

public sealed class MonitorCoordinator : IDisposable
{
    private readonly IMonitorCycleRunner runner;
    private readonly TimeSpan interval;
    private readonly TimeSpan watchdog;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly object snapshotGate = new object();
    private readonly object preferencesGate = new object();
    private Thread thread;
    private MonitorSnapshot latest;
    private UserPreferences preferences = UserPreferences.Defaults();
    private string lastAttentionKey;
    private int checkRequested = 1;
    private int paused;
    private int stopping;

    public MonitorCoordinator(IMonitorCycleRunner runner, TimeSpan interval, TimeSpan watchdog)
    {
        if (runner == null) throw new ArgumentNullException("runner");
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("interval");
        if (watchdog <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("watchdog");
        this.runner = runner;
        this.interval = interval;
        this.watchdog = watchdog;
        latest = MonitorSnapshot.CreateState(MonitorRunState.Starting, "正在启动", DateTime.UtcNow, DateTime.UtcNow);
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

    public void SetPaused(bool value)
    {
        Interlocked.Exchange(ref paused, value ? 1 : 0);
        if (value) Publish(MonitorSnapshot.CreateState(MonitorRunState.Paused, "自动优化已暂停",
            DateTime.UtcNow, DateTime.MaxValue), false);
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
            UserPreferences current;
            lock (preferencesGate) current = Copy(preferences);
            Task<MonitorSnapshot> cycle = Task.Factory.StartNew(() => runner.Run(current),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool completed = WaitWithoutThrowing(cycle, watchdog);
            if (!completed)
            {
                var stuck = MonitorSnapshot.CreateState(MonitorRunState.Stuck, "检测超时，正在等待当前操作退出",
                    DateTime.UtcNow, DateTime.UtcNow.Add(interval));
                Publish(stuck, true);
                while (Volatile.Read(ref stopping) == 0 && !WaitWithoutThrowing(cycle, TimeSpan.FromMilliseconds(100))) { }
            }
            if (Volatile.Read(ref stopping) != 0) break;

            if (cycle.IsFaulted)
            {
                Exception cause = cycle.Exception == null ? null : cycle.Exception.GetBaseException();
                var degraded = MonitorSnapshot.CreateState(MonitorRunState.Degraded,
                    cause == null ? "检测失败" : "检测失败：" + cause.Message,
                    DateTime.UtcNow, DateTime.UtcNow.Add(interval));
                Publish(degraded, true);
            }
            else if (cycle.IsCompleted)
            {
                Publish(cycle.Result, cycle.Result.State == MonitorRunState.Degraded || cycle.Result.State == MonitorRunState.Stuck);
            }
            nextRunUtc = DateTime.UtcNow.Add(interval);
        }
        Publish(MonitorSnapshot.CreateState(MonitorRunState.Stopped, "已退出", DateTime.UtcNow, DateTime.MaxValue), false);
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
            RequiredServices = new System.Collections.Generic.List<ServiceKind>(value.RequiredServices)
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        wake.Set();
        if (thread != null) thread.Join(TimeSpan.FromSeconds(2));
        wake.Dispose();
    }
}
