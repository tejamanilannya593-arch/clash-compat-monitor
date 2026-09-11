using System;
using System.Collections.Generic;
using System.Linq;

public enum MonitorRunState
{
    Starting,
    Checking,
    Pending,
    Running,
    Paused,
    Degraded,
    Stuck,
    Stopped
}

public sealed class ServiceMeasurement
{
    public ServiceMeasurement(ServiceKind service, bool available, long milliseconds, string detail, ProbeFailureKind evidence = ProbeFailureKind.None)
    {
        Service = service;
        Available = available;
        Milliseconds = milliseconds;
        Detail = detail ?? "";
        Evidence = evidence;
    }

    public ServiceKind Service { get; private set; }
    public bool Available { get; private set; }
    public long Milliseconds { get; private set; }
    public string Detail { get; private set; }
    public ProbeFailureKind Evidence { get; private set; }
}

public sealed class MonitorSnapshot
{
    private MonitorSnapshot() { }

    public MonitorRunState State { get; private set; }
    public string ActualNode { get; private set; }
    public double? Score { get; private set; }
    public string Decision { get; private set; }
    public string SelectionReason { get; private set; }
    public DateTime CheckedUtc { get; private set; }
    public DateTime NextCheckUtc { get; private set; }
    public IList<ServiceMeasurement> Services { get; private set; }

    public MonitorSnapshot WithState(MonitorRunState state, string decision, DateTime nextCheckUtc)
    {
        return new MonitorSnapshot { State = state, ActualNode = ActualNode, Score = Score,
            Decision = decision, SelectionReason = SelectionReason, CheckedUtc = CheckedUtc,
            NextCheckUtc = nextCheckUtc, Services = Services };
    }

    public MonitorSnapshot WithSelectionReason(string reason)
    {
        return new MonitorSnapshot { State = State, ActualNode = ActualNode, Score = Score,
            Decision = Decision, SelectionReason = reason ?? "", CheckedUtc = CheckedUtc,
            NextCheckUtc = NextCheckUtc, Services = Services };
    }

    public MonitorSnapshot WithProgress(string decision)
    {
        MonitorRunState progressState = State == MonitorRunState.Running ? MonitorRunState.Running : MonitorRunState.Checking;
        return WithState(progressState, decision, DateTime.MaxValue);
    }

    public static MonitorSnapshot CreateState(MonitorRunState state, string decision,
        DateTime checkedUtc, DateTime nextCheckUtc)
    {
        return new MonitorSnapshot {
            State = state,
            ActualNode = "",
            Decision = decision ?? "",
            SelectionReason = "",
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
            State = scan.Health == CandidateHealth.Unknown || scan.Health == CandidateHealth.BasicCompatible ? MonitorRunState.Pending :
                scan.Health == CandidateHealth.Compatible ? MonitorRunState.Running : MonitorRunState.Degraded,
            ActualNode = node ?? "",
            Score = score,
            Decision = decision ?? "",
            SelectionReason = "",
            CheckedUtc = checkedUtc,
            NextCheckUtc = nextCheckUtc,
            Services = scan.ServiceResults.OrderBy(pair => pair.Key)
                .Select(pair => new ServiceMeasurement(pair.Key, pair.Value.Passed,
                    pair.Value.ElapsedMilliseconds, pair.Value.Detail, pair.Value.FailureKind)).ToList().AsReadOnly()
        };
    }
}

public sealed class MonitorPresentation
{
    private MonitorPresentation() { }
    public string StateText { get; private set; }
    public string NodeText { get; private set; }
    public string DecisionText { get; private set; }
    public string ResponseText { get; private set; }

    public static MonitorPresentation From(MonitorSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException("snapshot");
        return new MonitorPresentation {
            StateText = StateLabel(snapshot.State),
            NodeText = String.IsNullOrWhiteSpace(snapshot.ActualNode) ? "尚未检测" : snapshot.ActualNode,
            DecisionText = String.IsNullOrWhiteSpace(snapshot.Decision) ? "等待检测" : StatusReport.DecisionText(snapshot.Decision),
            ResponseText = ResponseLabel(snapshot)
        };
    }

    private static string ResponseLabel(MonitorSnapshot snapshot)
    {
        if (snapshot.State == MonitorRunState.Degraded) return "综合响应：不可用";
        List<long> measured = snapshot.Services.Where(x => x.Available && x.Milliseconds > 0).Select(x => x.Milliseconds).ToList();
        if (measured.Count == 0) return "综合响应：待测";
        double average = measured.Average();
        return "综合响应：" + Math.Round(average).ToString("F0") + " ms · " + QualityPolicy.LatencyBand(average);
    }

    public static string ServiceText(bool available, long milliseconds, string detail)
    {
        if (!available) return "不可用";
        return milliseconds > 0 ? "可用 · " + milliseconds + " ms" : "可用";
    }

    public static string ServiceText(ServiceMeasurement service)
    {
        if (service.Evidence == ProbeFailureKind.Unverified) return "待验证";
        if (service.Evidence == ProbeFailureKind.Partial)
            return "仅确认可达" + (service.Milliseconds > 0 ? " · " + service.Milliseconds + " ms" : "");
        if (service.Evidence == ProbeFailureKind.Region) return "地区受限";
        if (service.Evidence == ProbeFailureKind.Transient) return "连接异常";
        return service.Available ? "探测通过" + (service.Milliseconds > 0 ? " · " + service.Milliseconds + " ms" : "") : "探测失败";
    }

    public static string ServiceLabel(ServiceKind service)
    {
        switch (service)
        {
            case ServiceKind.ChatGPT: return "ChatGPT";
            case ServiceKind.Gemini: return "Gemini";
            case ServiceKind.Google: return "Google";
            case ServiceKind.GitHub: return "GitHub";
            case ServiceKind.SteamStore: return "Steam 商店";
            case ServiceKind.SteamCommunity: return "Steam 社区";
            case ServiceKind.SteamApi: return "Steam API";
            case ServiceKind.Discord: return "Discord";
            case ServiceKind.Spotify: return "Spotify";
            default: return "Epic";
        }
    }

    private static string StateLabel(MonitorRunState state)
    {
        switch (state)
        {
            case MonitorRunState.Running: return "运行正常";
            case MonitorRunState.Checking: return "正在检测";
            case MonitorRunState.Pending: return "部分服务待验证";
            case MonitorRunState.Paused: return "自动优化已暂停";
            case MonitorRunState.Degraded: return "需要注意";
            case MonitorRunState.Stuck: return "检测超时";
            case MonitorRunState.Stopped: return "已退出";
            default: return "正在启动";
        }
    }
}
