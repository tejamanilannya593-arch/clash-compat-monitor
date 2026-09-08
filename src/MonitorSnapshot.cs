using System;
using System.Collections.Generic;
using System.Linq;

public enum MonitorRunState
{
    Starting,
    Running,
    Paused,
    Degraded,
    Stuck,
    Stopped
}

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
    private MonitorSnapshot() { }

    public MonitorRunState State { get; private set; }
    public string ActualNode { get; private set; }
    public double? Score { get; private set; }
    public string Decision { get; private set; }
    public DateTime CheckedUtc { get; private set; }
    public DateTime NextCheckUtc { get; private set; }
    public IList<ServiceMeasurement> Services { get; private set; }

    public static MonitorSnapshot CreateState(MonitorRunState state, string decision,
        DateTime checkedUtc, DateTime nextCheckUtc)
    {
        return new MonitorSnapshot {
            State = state,
            ActualNode = "",
            Decision = decision ?? "",
            CheckedUtc = checkedUtc,
            NextCheckUtc = nextCheckUtc,
            Services = new List<ServiceMeasurement>().AsReadOnly()
        };
    }

    public static MonitorSnapshot CreateRunning(string node, CandidateScanResult scan, double? score,
        string decision, DateTime checkedUtc, DateTime nextCheckUtc)
    {
        if (scan == null) throw new ArgumentNullException("scan");
        return new MonitorSnapshot {
            State = MonitorRunState.Running,
            ActualNode = node ?? "",
            Score = score,
            Decision = decision ?? "",
            CheckedUtc = checkedUtc,
            NextCheckUtc = nextCheckUtc,
            Services = scan.ServiceResults.OrderBy(pair => pair.Key)
                .Select(pair => new ServiceMeasurement(pair.Key, pair.Value.Passed,
                    pair.Value.ElapsedMilliseconds, pair.Value.Detail)).ToList().AsReadOnly()
        };
    }
}
