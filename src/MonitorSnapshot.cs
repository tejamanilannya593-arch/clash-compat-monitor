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

public sealed class MonitorPresentation
{
    private MonitorPresentation() { }
    public string StateText { get; private set; }
    public string NodeText { get; private set; }
    public string DecisionText { get; private set; }

    public static MonitorPresentation From(MonitorSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException("snapshot");
        return new MonitorPresentation {
            StateText = StateLabel(snapshot.State),
            NodeText = String.IsNullOrWhiteSpace(snapshot.ActualNode) ? "尚未检测" : snapshot.ActualNode,
            DecisionText = String.IsNullOrWhiteSpace(snapshot.Decision) ? "等待检测" : snapshot.Decision
        };
    }

    public static string ServiceText(bool available, long milliseconds, string detail)
    {
        if (!available) return "不可用";
        return milliseconds > 0 ? "可用 · " + milliseconds + " ms" : "可用";
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
            case MonitorRunState.Paused: return "自动优化已暂停";
            case MonitorRunState.Degraded: return "需要注意";
            case MonitorRunState.Stuck: return "检测超时";
            case MonitorRunState.Stopped: return "已退出";
            default: return "正在启动";
        }
    }
}
