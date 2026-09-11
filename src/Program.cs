using System;
using System.IO;
using System.Threading;

public static class MonitorIdentity
{
    public const string Name = "ClashCompatibilityMonitor";
    public const string Version = "0.1.0";
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        MonitorOptions options = MonitorOptions.Parse(args);
        MonitorConfiguration config = MonitorConfiguration.CreateDefault();
        var logger = new BoundedLogger(config.LogPath, 1024 * 1024);
        try
        {
            var client = MihomoPipeClient.FromConfig(config.ClashConfigPath);
            if (options.SelfTest)
            {
                if (!File.Exists(config.ClashConfigPath) || !client.IsAvailable()) throw new InvalidOperationException("Mihomo configuration or pipe is unavailable.");
                logger.Write("self-test passed");
                Environment.ExitCode = 0;
                return;
            }
            using (var lease = SingleInstanceLease.TryAcquire(@"Local\ClashCompatibilityMonitor"))
            {
                if (lease == null) { Environment.ExitCode = 2; return; }
                DateTime startupDeadline = DateTime.UtcNow.AddSeconds(90);
                while (!client.IsAvailable() && DateTime.UtcNow < startupDeadline) Thread.Sleep(2000);
                if (!client.IsAvailable()) throw new InvalidOperationException("Mihomo pipe did not become available during startup.");
                if (client.IsRuntimeIpv6Enabled() && !client.EnsureIpv4Compatibility(config.ClashConfigPath))
                    throw new InvalidOperationException("Mihomo rejected the in-memory IPv4 compatibility configuration.");
                using (var probe = new HttpServiceProbe(config.ProbeProxy))
                {
                    var worker = new MonitorWorker(config, client, probe, logger, new SystemClock());
                    if (options.Once) worker.RunOnce(options.DryRun);
                    else while (true)
                    {
                        try { worker.RunOnce(false); }
                        catch (Exception cycleError) { logger.Write("cycle-error " + cycleError.GetType().Name + ": " + cycleError.Message); }
                        Thread.Sleep(config.CycleInterval);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Write("fatal " + ex.GetType().Name + ": " + ex.Message);
            Environment.ExitCode = 1;
        }
    }
}

public sealed class MonitorOptions
{
    public bool DryRun { get; private set; }
    public bool Once { get; private set; }
    public bool SelfTest { get; private set; }
    public static MonitorOptions Parse(string[] args)
    {
        var result = new MonitorOptions();
        foreach (string arg in args ?? new string[0])
        {
            if (arg == "--dry-run") result.DryRun = true;
            else if (arg == "--once") result.Once = true;
            else if (arg == "--self-test") { result.SelfTest = true; result.Once = true; }
        }
        return result;
    }
}

public sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex mutex;
    private readonly bool owns;
    private SingleInstanceLease(Mutex mutex, bool owns) { this.mutex = mutex; this.owns = owns; }
    public static SingleInstanceLease TryAcquire(string name)
    {
        bool created;
        var mutex = new Mutex(true, name, out created);
        if (!created) { mutex.Dispose(); return null; }
        return new SingleInstanceLease(mutex, true);
    }
    public void Dispose() { if (owns) mutex.ReleaseMutex(); mutex.Dispose(); }
}

public sealed class MonitorConfiguration
{
    public TimeSpan CycleInterval = TimeSpan.FromSeconds(60);
    public TimeSpan MinimumHold = TimeSpan.FromMinutes(10);
    public string SharedGroup = "🌐 统一稳定节点";
    public string ProbeGroup = "🧪 兼容性探测";
    public string ProbeProxy = "http://127.0.0.1:7896";
    public string RootPath;
    public string StatePath;
    public string QualityStatePath;
    public string LogPath;
    public string ClashConfigPath;
    public string DelayProbeUrl = "https://www.gstatic.com/generate_204";
    public string ThroughputProbeUrl = "https://speed.cloudflare.com/__down?bytes=1048576";
    public TimeSpan QualityRefreshInterval = TimeSpan.FromHours(6);
    public static MonitorConfiguration CreateDefault()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string root = Path.Combine(local, "ClashCompatibilityMonitor");
        return new MonitorConfiguration {
            RootPath = root,
            StatePath = Path.Combine(root, "state", "health.state"),
            QualityStatePath = Path.Combine(root, "state", "quality.state"),
            LogPath = Path.Combine(root, "logs", "monitor.log"),
            ClashConfigPath = Path.Combine(roaming, "io.github.clash-verge-rev.clash-verge-rev", "clash-verge.yaml")
        };
    }
}
