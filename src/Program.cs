using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class MonitorIdentity
{
    public const string Name = "ClashCompatibilityMonitor";
    public const string Version = "0.7.0-preview.27";
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        MonitorOptions options = MonitorOptions.Parse(args);
        MonitorConfiguration config = MonitorConfiguration.CreateDefault();
        var logger = new BoundedLogger(config.LogPath, 1024 * 1024);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs error) {
            LogFailure(logger, "ui-thread", error.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs error) {
            LogFailure(logger, error.IsTerminating ? "appdomain-terminating" : "appdomain",
                error.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs error) {
            LogFailure(logger, "unobserved-task", error.Exception);
            error.SetObserved();
        };
        logger.TryWrite("process started version=" + MonitorIdentity.Version);
        try
        {
            if (options.StartupSupervisor)
            {
                Environment.ExitCode = StartupSupervision.Run(
                    Application.ExecutablePath, AppDomain.CurrentDomain.BaseDirectory, logger);
                return;
            }

            byte[] identityKey = ExitIdentityKey.LoadOrCreate(config.IdentityKeyPath);
            var nodeIdentities = new ClashNodeIdentitySource(
                config.ClashConfigPath, config.ClashProfilesPath, identityKey);
            if (options.SelfTest)
            {
                var client = MihomoPipeClient.FromConfig(config.ClashConfigPath);
                if (!File.Exists(config.ClashConfigPath) || !client.IsAvailable()) throw new InvalidOperationException("Mihomo configuration or pipe is unavailable.");
                logger.Write("self-test passed");
                Environment.ExitCode = 0;
                return;
            }

            if (options.Once)
            {
                var headlessClient = MihomoPipeClient.FromConfig(config.ClashConfigPath);
                using (var lease = SingleInstanceLease.TryAcquire(@"Local\ClashCompatibilityMonitor"))
                {
                    if (lease == null) { Environment.ExitCode = 2; return; }
                    if (!headlessClient.IsAvailable()) throw new InvalidOperationException("Mihomo pipe is unavailable.");
                    using (var probe = new HttpServiceProbe(config.ProbeProxy))
                    {
                        var exitProbe = new CloudflareExitIdentityProbe(
                            config.ProbeProxy, identityKey, config.ExitNetworkEvidencePath);
                        var worker = new MonitorWorker(config, headlessClient, probe, logger, new SystemClock(), exitProbe,
                            new ProxyPathHealthChecker(config.ProbeProxy, "http://127.0.0.1:7897"), nodeIdentities);
                        worker.RunOnce(options.DryRun, new UserPreferenceStore(config.PreferencesPath).Load());
                    }
                }
                return;
            }

            using (var activation = InstanceActivation.TryOwn(@"Local\ClashCompatibilityMonitor",
                ex => LogFailure(logger, "activation-callback", ex)))
            {
                if (!activation.IsOwner) { Environment.ExitCode = 0; return; }
                var client = MihomoPipeClient.FromConfig(config.ClashConfigPath);
                var preferenceStore = new UserPreferenceStore(config.PreferencesPath);
                UserPreferences preferences = preferenceStore.Load();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var probe = new HttpServiceProbe(config.ProbeProxy))
                {
                    var exitProbe = new CloudflareExitIdentityProbe(
                        config.ProbeProxy, identityKey, config.ExitNetworkEvidencePath);
                    var worker = new MonitorWorker(config, client, probe, logger, new SystemClock(), exitProbe,
                        new ProxyPathHealthChecker(config.ProbeProxy, "http://127.0.0.1:7897"), nodeIdentities);
                    using (var coordinator = new MonitorCoordinator(worker, config.CycleInterval,
                        config.CycleWatchdog, true, errorReporter: ex =>
                            LogFailure(logger, "coordinator", ex)))
                    using (var tray = new TrayHost(coordinator, preferenceStore, preferences))
                    {
                        activation.Activated += tray.ShowDetailsFromAnyThread;
                        activation.StartListening();
                        coordinator.UpdatePreferences(preferences);
                        coordinator.Start();
                        tray.ShowInitialIfNeeded();
                        Application.Run(tray);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogFailure(logger, "main-fatal", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            logger.TryWrite("process stopped exit_code=" + Environment.ExitCode);
        }
    }

    private static void LogFailure(BoundedLogger logger, string source, Exception error)
    {
        string detail = error == null ? "unknown exception" : error.ToString();
        logger.TryWrite("failure source=" + source + " " + detail);
    }
}

public sealed class MonitorOptions
{
    public bool DryRun { get; private set; }
    public bool Once { get; private set; }
    public bool SelfTest { get; private set; }
    public bool StartupSupervisor { get; private set; }
    public static MonitorOptions Parse(string[] args)
    {
        var result = new MonitorOptions();
        foreach (string arg in args ?? new string[0])
        {
            if (arg == "--dry-run") result.DryRun = true;
            else if (arg == "--once") result.Once = true;
            else if (arg == "--self-test") { result.SelfTest = true; result.Once = true; }
            else if (arg == "--startup-supervisor") result.StartupSupervisor = true;
        }
        return result;
    }
}

public static class StartupSupervision
{
    public static bool ShouldRestart(int exitCode, int abnormalExitCount)
    {
        return exitCode != 0 && abnormalExitCount < 3;
    }

    public static int Run(string executablePath, string workingDirectory, BoundedLogger logger)
    {
        int abnormalExitCount = 0;
        while (true)
        {
            var start = new ProcessStartInfo {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            int exitCode;
            using (Process child = Process.Start(start))
            {
                if (child == null) throw new InvalidOperationException("Unable to start the monitor process.");
                child.WaitForExit();
                exitCode = child.ExitCode;
            }
            if (exitCode == 0) return 0;
            abnormalExitCount++;
            logger.TryWrite("startup supervisor observed exit_code=" + exitCode +
                " abnormal_exit_count=" + abnormalExitCount);
            if (!ShouldRestart(exitCode, abnormalExitCount)) return exitCode;
            Thread.Sleep(5000);
        }
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
    public string GeneralGroup = "🚀 节点选择";
    public string ProbeGroup = "🧪 兼容性探测";
    public string ProbeProxy = "http://127.0.0.1:7896";
    public string RootPath;
    public string StatePath;
    public string QualityStatePath;
    public string PreferencesPath;
    public string ExitNetworkEvidencePath;
    public string LogPath;
    public string ClashConfigPath;
    public string ClashProfilesPath;
    public string IdentityKeyPath;
    public string DelayProbeUrl = "https://www.gstatic.com/generate_204";
    public string ThroughputProbeUrl = "https://speed.cloudflare.com/__down?bytes=1048576";
    public TimeSpan QualityRefreshInterval = TimeSpan.FromHours(6);
    public TimeSpan ReloadRecoveryFreshness = TimeSpan.FromMinutes(30);
    public TimeSpan CycleWatchdog = TimeSpan.FromMinutes(8);
    public static MonitorConfiguration CreateDefault()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string root = Path.Combine(local, "ClashCompatibilityMonitor");
        return new MonitorConfiguration {
            RootPath = root,
            StatePath = Path.Combine(root, "state", "health.state"),
            QualityStatePath = Path.Combine(root, "state", "quality.state"),
            PreferencesPath = Path.Combine(root, "state", "preferences.state"),
            ExitNetworkEvidencePath = Path.Combine(root, "state", "exit-network.state"),
            LogPath = Path.Combine(root, "logs", "monitor.log"),
            ClashConfigPath = Path.Combine(roaming, "io.github.clash-verge-rev.clash-verge-rev", "clash-verge.yaml"),
            ClashProfilesPath = Path.Combine(roaming, "io.github.clash-verge-rev.clash-verge-rev", "profiles.yaml"),
            IdentityKeyPath = Path.Combine(root, "state", "identity.key")
        };
    }
}
