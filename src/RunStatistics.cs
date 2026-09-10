using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

public sealed class StatisticsReport
{
    public string Version { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime CapturedUtc { get; set; }
    public long CompletedCycles { get; set; }
    public long AttentionCycles { get; set; }
    public long ConfirmedSelections { get; set; }
    public long ReceivedBodyBytes { get; set; }
    public double LastCycleSeconds { get; set; }
    public double MaximumCycleSeconds { get; set; }
    public double WorkingSetMiB { get; set; }
    public double PrivateMemoryMiB { get; set; }
    public double CpuSeconds { get; set; }
}

public static class RunStatistics
{
    private static readonly DateTime started = DateTime.UtcNow;
    private static readonly object gate = new object();
    private static long cycles, attention, selections, bodyBytes;
    private static double lastSeconds, maximumSeconds;
    public static void AddBodyBytes(long count) { if (count > 0) Interlocked.Add(ref bodyBytes, count); }
    public static void SelectionConfirmed() { Interlocked.Increment(ref selections); }
    public static void CycleCompleted(MonitorRunState state, double seconds)
    {
        lock (gate)
        {
            cycles++;
            if (state == MonitorRunState.Degraded || state == MonitorRunState.Stuck) attention++;
            lastSeconds = Math.Max(0, seconds); maximumSeconds = Math.Max(maximumSeconds, lastSeconds);
        }
    }
    public static StatisticsReport Capture()
    {
        using (var process = Process.GetCurrentProcess())
        lock (gate)
            return new StatisticsReport { Version = MonitorIdentity.Version, StartedUtc = started, CapturedUtc = DateTime.UtcNow,
                CompletedCycles = cycles, AttentionCycles = attention, ConfirmedSelections = Interlocked.Read(ref selections),
                ReceivedBodyBytes = Interlocked.Read(ref bodyBytes), LastCycleSeconds = lastSeconds, MaximumCycleSeconds = maximumSeconds,
                WorkingSetMiB = process.WorkingSet64 / 1048576.0, PrivateMemoryMiB = process.PrivateMemorySize64 / 1048576.0,
                CpuSeconds = process.TotalProcessorTime.TotalSeconds };
    }
    public static void SaveLocal()
    {
        try { StatusReport.WriteAtomic(Path.Combine(MonitorConfiguration.CreateDefault().RootPath, "runtime-statistics.json"),
            new JavaScriptSerializer().Serialize(Capture())); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
