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

public enum BrowserVerificationStatus
{
    None,
    CompanionOffline,
    Ready,
    Running,
    Passed,
    SignInRequired,
    ChallengeRequired,
    AutomationUnsupported,
    ConversationError,
    GenerationTimeout,
    Cancelled
}

public sealed class ServiceMeasurement
{
    public ServiceMeasurement(ServiceKind service, bool available, long milliseconds, string detail,
        ProbeFailureKind evidence = ProbeFailureKind.None, bool accountVerified = false,
        DateTime accountVerifiedUtc = default(DateTime))
    {
        Service = service;
        Available = available;
        Milliseconds = milliseconds;
        Detail = detail ?? "";
        Evidence = evidence;
        AccountVerified = accountVerified;
        AccountVerifiedUtc = accountVerifiedUtc;
    }

    public ServiceKind Service { get; private set; }
    public bool Available { get; private set; }
    public long Milliseconds { get; private set; }
    public string Detail { get; private set; }
    public ProbeFailureKind Evidence { get; private set; }
    public bool AccountVerified { get; private set; }
    public DateTime AccountVerifiedUtc { get; private set; }
}

public sealed class CandidateLatencyMeasurement
{
    public CandidateLatencyMeasurement(string node, string exitCountryCode, int mihomoMilliseconds,
        IEnumerable<ServiceMeasurement> services, DateTime checkedUtc, string decisionDetail = "")
    {
        Node = node ?? "";
        ExitCountryCode = exitCountryCode ?? "";
        MihomoMilliseconds = mihomoMilliseconds;
        Services = (services ?? Enumerable.Empty<ServiceMeasurement>()).ToList().AsReadOnly();
        CheckedUtc = checkedUtc;
        DecisionDetail = decisionDetail ?? "";
    }

    public string Node { get; private set; }
    public string ExitCountryCode { get; private set; }
    public int MihomoMilliseconds { get; private set; }
    public IList<ServiceMeasurement> Services { get; private set; }
    public DateTime CheckedUtc { get; private set; }
    public string DecisionDetail { get; private set; }

    public CandidateLatencyMeasurement WithDecisionDetail(string value)
    {
        return new CandidateLatencyMeasurement(Node, ExitCountryCode, MihomoMilliseconds,
            Services, CheckedUtc, value);
    }
}

internal static class CandidateDecisionText
{
    public static string Initial(CandidateLatencyMeasurement candidate)
    {
        return Initial(candidate, null);
    }

    public static string Initial(CandidateLatencyMeasurement candidate,
        IEnumerable<ServiceMeasurement> currentServices)
    {
        if (candidate == null) return "等待实测";
        string failed = FailedService(candidate.Services);
        if (failed.Length != 0) return failed + " 未通过";
        double baseline = WebsitePriorityLatency.Primary(currentServices);
        double target = WebsitePriorityLatency.Primary(candidate);
        if (baseline == Double.MaxValue || target == Double.MaxValue)
            return "等待完整复检";
        long improvement = (long)Math.Round(baseline - target);
        if (improvement > 0)
            return "快 " + improvement + " ms；后台全节点寻优会重新复检";
        if (improvement == 0) return "与当前节点实测相同；无需切换";
        return "比当前慢 " + (-improvement) + " ms；无需切换";
    }

    public static string FullRecheck(CandidateScanResult scan,
        IEnumerable<ServiceKind> requiredServices)
    {
        if (scan == null) return "完整复检未完成";
        if (!AiRegionPolicy.SupportsChatGpt(scan.ExitCountryCode))
            return "出口地区不符合 ChatGPT 要求";
        var required = (requiredServices ?? Enumerable.Empty<ServiceKind>())
            .Concat(CoreWebsitePolicy.Required)
            .Distinct().ToList();
        foreach (ServiceKind service in required)
        {
            ProbeResult result;
            if (!scan.ServiceResults.TryGetValue(service, out result) || result == null)
                return MonitorPresentation.ServiceLabel(service) + " 未完成复检";
            if (!result.Passed && result.FailureKind != ProbeFailureKind.Partial)
                return MonitorPresentation.ServiceLabel(service) + " 完整复检未通过";
        }
        return "完整复检未达到切换条件";
    }

    private static string FailedService(IEnumerable<ServiceMeasurement> services)
    {
        IList<ServiceMeasurement> values = (services ?? Enumerable.Empty<ServiceMeasurement>()).ToList();
        IEnumerable<ServiceKind> ordered = CoreWebsitePolicy.Required;
        foreach (ServiceKind service in ordered)
        {
            ServiceMeasurement measurement = values.FirstOrDefault(x => x.Service == service);
            if (measurement == null || !measurement.Available ||
                measurement.Evidence == ProbeFailureKind.Unverified || measurement.Evidence == ProbeFailureKind.Partial)
                return MonitorPresentation.ServiceLabel(service);
        }
        return "";
    }
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
    public IList<CandidateLatencyMeasurement> CandidateLatencies { get; private set; }
    public CandidateHealth Health { get; private set; }
    public string ExitFingerprint { get; private set; }
    public string ExitCountryCode { get; private set; }
    public BrowserVerificationStatus BrowserStatus { get; private set; }
    public string BrowserStatusDetail { get; private set; }

    public MonitorSnapshot WithState(MonitorRunState state, string decision, DateTime nextCheckUtc)
    {
        return new MonitorSnapshot { State = state, ActualNode = ActualNode, Score = Score,
            Decision = decision, SelectionReason = SelectionReason, CheckedUtc = CheckedUtc,
            NextCheckUtc = nextCheckUtc, Services = Services, CandidateLatencies = CandidateLatencies,
            ExitFingerprint = ExitFingerprint,
            ExitCountryCode = ExitCountryCode, Health = Health, BrowserStatus = BrowserStatus,
            BrowserStatusDetail = BrowserStatusDetail };
    }

    public MonitorSnapshot WithSelectionReason(string reason)
    {
        return new MonitorSnapshot { State = State, ActualNode = ActualNode, Score = Score,
            Decision = Decision, SelectionReason = reason ?? "", CheckedUtc = CheckedUtc,
            NextCheckUtc = NextCheckUtc, Services = Services, CandidateLatencies = CandidateLatencies,
            ExitFingerprint = ExitFingerprint,
            ExitCountryCode = ExitCountryCode, Health = Health, BrowserStatus = BrowserStatus,
            BrowserStatusDetail = BrowserStatusDetail };
    }

    public MonitorSnapshot WithBrowserStatus(BrowserVerificationStatus status, string detail)
    {
        return new MonitorSnapshot { State = State, ActualNode = ActualNode, Score = Score,
            Decision = Decision, SelectionReason = SelectionReason, CheckedUtc = CheckedUtc,
            NextCheckUtc = NextCheckUtc, Services = Services, CandidateLatencies = CandidateLatencies,
            ExitFingerprint = ExitFingerprint,
            ExitCountryCode = ExitCountryCode, Health = Health, BrowserStatus = status,
            BrowserStatusDetail = detail ?? "" };
    }

    public MonitorSnapshot WithProgress(string decision)
    {
        MonitorRunState progressState = State == MonitorRunState.Running ? MonitorRunState.Running : MonitorRunState.Checking;
        return WithState(progressState, decision, DateTime.MaxValue);
    }

    public MonitorSnapshot WithCandidateLatencies(IEnumerable<CandidateLatencyMeasurement> values)
    {
        return new MonitorSnapshot { State = State, ActualNode = ActualNode, Score = Score,
            Decision = Decision, SelectionReason = SelectionReason, CheckedUtc = CheckedUtc,
            NextCheckUtc = NextCheckUtc, Services = Services,
            CandidateLatencies = (values ?? Enumerable.Empty<CandidateLatencyMeasurement>()).ToList().AsReadOnly(),
            ExitFingerprint = ExitFingerprint, ExitCountryCode = ExitCountryCode, Health = Health,
            BrowserStatus = BrowserStatus, BrowserStatusDetail = BrowserStatusDetail };
    }

    public MonitorSnapshot WithAccountVerification(ExperienceData data, string scope, DateTime now)
    {
        var services = Services.Select(service => {
            AccountVerificationRecord record = (service.Service == ServiceKind.ChatGPT || service.Service == ServiceKind.Gemini)
                ? AccountVerificationMemory.FindBrowserConversationValid(data, scope, ActualNode,
                    ExitFingerprint, service.Service, now, BrowserConversationProof.CurrentProtocolVersion) : null;
            return new ServiceMeasurement(service.Service, service.Available, service.Milliseconds,
                service.Detail, service.Evidence, record != null,
                record == null ? DateTime.MinValue : record.VerifiedUtc);
        }).ToList().AsReadOnly();
        bool accountReady = !services.Any(x => (x.Service == ServiceKind.ChatGPT || x.Service == ServiceKind.Gemini) &&
            x.Available && x.Evidence == ProbeFailureKind.None && !x.AccountVerified);
        MonitorRunState state = Health == CandidateHealth.Compatible
            ? (accountReady ? MonitorRunState.Running : MonitorRunState.Pending) : State;
        return new MonitorSnapshot { State = state, ActualNode = ActualNode, Score = Score,
            Decision = Decision, SelectionReason = SelectionReason, CheckedUtc = CheckedUtc,
            NextCheckUtc = NextCheckUtc, Services = services, CandidateLatencies = CandidateLatencies,
            ExitFingerprint = ExitFingerprint,
            ExitCountryCode = ExitCountryCode, Health = Health, BrowserStatus = BrowserStatus,
            BrowserStatusDetail = BrowserStatusDetail };
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
            Health = CandidateHealth.Unknown,
            BrowserStatus = BrowserVerificationStatus.None,
            BrowserStatusDetail = "",
            Services = new List<ServiceMeasurement>().AsReadOnly(),
            CandidateLatencies = new List<CandidateLatencyMeasurement>().AsReadOnly()
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
            Health = scan.Health,
            BrowserStatus = BrowserVerificationStatus.None,
            BrowserStatusDetail = "",
            Services = scan.ServiceResults.OrderBy(pair => pair.Key)
                .Select(pair => new ServiceMeasurement(pair.Key, pair.Value.Passed,
                    pair.Value.ElapsedMilliseconds, pair.Value.Detail, pair.Value.FailureKind)).ToList().AsReadOnly(),
            CandidateLatencies = new List<CandidateLatencyMeasurement>().AsReadOnly(),
            ExitFingerprint = scan.ExitFingerprint ?? "",
            ExitCountryCode = scan.ExitCountryCode ?? ""
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
            StateText = StateLabel(snapshot),
            NodeText = String.IsNullOrWhiteSpace(snapshot.ActualNode) ? "尚未检测" : snapshot.ActualNode,
            DecisionText = String.IsNullOrWhiteSpace(snapshot.Decision) ? "等待检测" : StatusReport.DecisionText(snapshot.Decision),
            ResponseText = ResponseLabel(snapshot)
        };
    }

    private static string ResponseLabel(MonitorSnapshot snapshot)
    {
        if (snapshot.State == MonitorRunState.Degraded) return "综合响应：不可用";
        List<ServiceMeasurement> measuredServices = snapshot.Services.Where(x => x.Available && x.Milliseconds > 0).ToList();
        List<long> measured = measuredServices.Select(x => x.Milliseconds).ToList();
        if (measured.Count == 0) return "综合响应：待测";
        double p75 = QualityMeasurement.Percentile75(measured.Select(x => (double)x), Double.NaN);
        string label = "综合响应：" + Math.Round(p75).ToString("F0") + " ms · " + LatencyBand(p75);
        ServiceMeasurement slowest = measuredServices.OrderByDescending(x => x.Milliseconds).First();
        if (slowest.Milliseconds > 2000)
            label += " · " + ServiceLabel(slowest.Service) + " " + slowest.Milliseconds + " ms";
        return label;
    }

    public static string ServiceText(bool available, long milliseconds, string detail)
    {
        if (!available) return "不可用";
        return milliseconds > 0 ? "可用 · " + milliseconds + " ms" : "可用";
    }

    public static string ServiceText(ServiceMeasurement service)
    {
        if (service.Evidence == ProbeFailureKind.Unverified) return "待验证";
        if (service.Service == ServiceKind.ZLibraryWeb && service.Evidence == ProbeFailureKind.Partial)
            return "网站功能未验证" + (service.Milliseconds > 0 ? " · " + service.Milliseconds + " ms" : "");
        if (service.Evidence == ProbeFailureKind.Partial)
            return "仅确认可达" + (service.Milliseconds > 0 ? " · " + service.Milliseconds + " ms" : "");
        if (service.Evidence == ProbeFailureKind.Region) return "地区受限";
        if (service.Evidence == ProbeFailureKind.Transient) return "连接异常";
        if (service.Service == ServiceKind.ZLibraryWeb && service.Available && service.Evidence == ProbeFailureKind.None)
            return "入口可达" + (service.Milliseconds > 0 ? " · " + service.Milliseconds + " ms" : "");
        if ((service.Service == ServiceKind.ChatGPT || service.Service == ServiceKind.Gemini) &&
            service.Available && service.Evidence == ProbeFailureKind.None)
            return service.AccountVerified
                ? "真实对话已验证" + (service.Milliseconds > 0 ? " · " + service.Milliseconds + " ms" : "") +
                    " · 有效至 " + service.AccountVerifiedUtc.AddDays(30).ToString("MM-dd")
                : "网络链路兼容 · 真实对话待验证" +
                    (service.Milliseconds > 0 ? " · " + service.Milliseconds + " ms" : "");
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
            case ServiceKind.ZLibraryWeb: return "Z-Library 网页";
            default: return "Epic";
        }
    }

    private static string StateLabel(MonitorSnapshot snapshot)
    {
        if (snapshot.BrowserStatus == BrowserVerificationStatus.Running) return "正在验证 AI 对话";
        if (snapshot.BrowserStatus == BrowserVerificationStatus.SignInRequired) return "需要浏览器登录";
        if (snapshot.BrowserStatus == BrowserVerificationStatus.AutomationUnsupported) return "浏览器自动化暂不支持";
        if (snapshot.Health == CandidateHealth.BasicCompatible) return "基础连接可用";
        if (snapshot.State == MonitorRunState.Pending && snapshot.Health == CandidateHealth.Compatible)
            return "基础连接可用";
        switch (snapshot.State)
        {
            case MonitorRunState.Running: return "运行正常";
            case MonitorRunState.Checking: return "正在检测";
            case MonitorRunState.Pending: return "部分服务待验证";
            case MonitorRunState.Paused: return "节点守护已暂停";
            case MonitorRunState.Degraded: return "需要注意";
            case MonitorRunState.Stuck: return "检测超时";
            case MonitorRunState.Stopped: return "已退出";
            default: return "正在启动";
        }
    }

    private static string LatencyBand(double milliseconds)
    {
        if (Double.IsNaN(milliseconds) || Double.IsInfinity(milliseconds) || milliseconds < 0) return "待测";
        if (milliseconds <= 300) return "优秀";
        if (milliseconds <= 500) return "良好";
        if (milliseconds <= 800) return "可用但偏慢";
        return "较慢";
    }
}
