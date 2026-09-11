using System;
using System.Globalization;
using System.IO;
using System.Text;

public static class StatusReport
{
    public static string Format(DateTime checkedUtc, string node, CandidateHealth health, double? score,
        string decision, string detail)
    {
        return "版本：" + MonitorIdentity.Version + Environment.NewLine +
            "检测时间（UTC）：" + checkedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine +
            "实际节点：" + (node ?? "(none)") + Environment.NewLine +
            "检测状态：" + HealthText(health) + Environment.NewLine +
            "综合分：" + (score.HasValue ? score.Value.ToString("F1", CultureInfo.InvariantCulture) : "本轮未排名") + Environment.NewLine +
            "决定：" + DecisionText(decision) + Environment.NewLine +
            "说明：" + (detail ?? "无") + Environment.NewLine +
            "基础可用或待验证均不代表账号级功能已完整验证。";
    }

    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content ?? "", new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private static string HealthText(CandidateHealth health)
    {
        switch (health)
        {
            case CandidateHealth.Compatible: return "完整检测通过";
            case CandidateHealth.BasicCompatible: return "基础可用";
            case CandidateHealth.RegionBlocked: return "地区受限";
            case CandidateHealth.ServiceFailed: return "服务失败";
            case CandidateHealth.Transient: return "临时网络错误";
            default: return "待验证";
        }
    }

    public static string DecisionText(string decision)
    {
        if (decision != null && decision.StartsWith("已切换：", StringComparison.Ordinal))
            return "已切换：" + DecisionText(decision.Substring(4));
        switch (decision)
        {
            case "quality difference below threshold": return "质量提升不足 20%，保持当前节点";
            case "quality improved by at least 20 percent": return "综合质量至少提升 20%";
            case "minimum hold": return "仍在最短持有期，保持当前节点";
            case "target requires fresh verification": return "目标节点需要重新验证";
            case "target requires proven stability": return "候选节点尚未积累 5 次、跨度 30 分钟且成功率不低于 95% 的历史";
            case "target requires account verification": return "候选节点尚未完成所选 AI 服务的账号实测，不进行性能切换";
            case "target requires strict service evidence": return "候选节点仅确认基础可达，不进行自动切换";
            case "provisional emergency failover": return "当前节点故障，临时切换到登录链路已通过但尚未账号实测的节点";
            case "current entry reachable but login unverified": return "当前节点仅确认入口可达，登录与对话仍待验证";
            case "current response already preferred": return "当前节点响应不超过 800 ms，保持当前节点";
            case "target response exceeds preferred threshold": return "候选节点响应超过自动寻优标准，不进行性能切换";
            case "current response not persistently slow": return "当前节点最近 3 次中位响应未超过 800 ms，保持当前节点";
            case "target response history not preferred": return "候选节点最近 5 次延迟未达到中位数不超过 800 ms、单次不超过 1500 ms 的标准";
            case "target service response exceeds limit": return "候选节点存在超过 1500 ms 的服务响应";
            case "current healthy": return "当前节点正常";
            case "awaiting confirmation": return "等待第二次失败确认";
            case "no compatible candidate": return "没有已验证的替代节点";
            case "confirmed failure": return "当前节点连续检测失败";
            case "total disconnect": return "当前节点完全断开";
            case "current disconnected": return "当前节点已断开";
            default: return string.IsNullOrWhiteSpace(decision) ? "保持当前节点" : decision;
        }
    }
}
