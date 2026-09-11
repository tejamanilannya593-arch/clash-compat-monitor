using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

public sealed class NodeExperience
{
    public string Scope { get; set; }
    public string Node { get; set; }
    public DateTime FirstUtc { get; set; }
    public DateTime LastUtc { get; set; }
    public int Samples { get; set; }
    public double Success { get; set; }
    public double ResponseMs { get; set; }
    public double JitterMs { get; set; }
    public double Throughput { get; set; }
    public DateTime SpeedUtc { get; set; }
    public bool LastPassed { get; set; }
    public DateTime FirstOutcomeUtc { get; set; }
    public DateTime LastOutcomeUtc { get; set; }
    public int OutcomeSamples { get; set; }
    public int SuccessfulSamples { get; set; }
    public List<double> RecentResponseMilliseconds { get; set; }
    public double Rank(DateTime now)
    {
        double speed = SpeedUtc >= now.AddDays(-1) && Throughput > 0 ? 10 * Throughput / (Throughput + 1048576) : 0;
        return 65 * Success + 20 * 300 / (300 + Math.Max(0, ResponseMs)) +
            5 * 100 / (100 + Math.Max(0, JitterMs)) + speed;
    }
}

public sealed class NodeChange
{
    public DateTime Utc { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public string Reason { get; set; }
}

public sealed class ExperienceData
{
    public ConnectionAssurance Assurance { get; set; }
    public string ActiveScope { get; set; }
    public string ActiveServicesKey { get; set; }
    public List<string> ActiveCandidateNames { get; set; }
    public string ScopeContinuityReason { get; set; }
    public string LastNode { get; set; }
    public List<NodeExperience> Nodes { get; set; }
    public List<NodeChange> Changes { get; set; }
    public List<ServiceIncidentRecord> ServiceIncidents { get; set; }
    public List<AccountVerificationRecord> AccountVerifications { get; set; }
    public ExperienceData()
    {
        ActiveCandidateNames = new List<string>();
        Nodes = new List<NodeExperience>();
        Changes = new List<NodeChange>();
        ServiceIncidents = new List<ServiceIncidentRecord>();
        AccountVerifications = new List<AccountVerificationRecord>();
    }

    public string ResolveScope(string fingerprint, IEnumerable<string> candidates, string servicesKey)
    {
        var current = (candidates ?? Enumerable.Empty<string>()).Where(x => !String.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var previous = (ActiveCandidateNames ?? new List<string>()).Where(x => !String.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        string normalizedServices = servicesKey ?? "";
        string previousServices = ActiveServicesKey;
        if (String.IsNullOrEmpty(previousServices) && !String.IsNullOrEmpty(ActiveScope))
        {
            int separator = ActiveScope.LastIndexOf('|');
            if (separator >= 0) previousServices = ActiveScope.Substring(separator + 1);
        }
        if (previous.Count == 0 && !String.IsNullOrEmpty(ActiveScope))
            previous = Nodes.Where(x => x != null && x.Scope == ActiveScope && !String.IsNullOrWhiteSpace(x.Node))
                .Select(x => x.Node).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        bool sameServices = String.Equals(previousServices, normalizedServices, StringComparison.Ordinal);
        bool exact = previous.Count > 0 && previous.SequenceEqual(current, StringComparer.Ordinal);
        int overlap = previous.Intersect(current, StringComparer.Ordinal).Count();
        int smaller = Math.Min(previous.Count, current.Count);
        bool compatibleUpdate = smaller >= 2 && overlap >= 2 && (double)overlap / smaller >= 0.60;
        bool legacyServiceRemoval = !sameServices && (exact || compatibleUpdate) &&
            String.Equals(RemoveLegacyJmComic(previousServices), normalizedServices, StringComparison.Ordinal);
        bool preserve = !String.IsNullOrEmpty(ActiveScope) && (sameServices || legacyServiceRemoval) &&
            (exact || compatibleUpdate);

        if (legacyServiceRemoval && preserve)
        {
            string oldScope = ActiveScope;
            ActiveScope = (fingerprint ?? "") + "|" + normalizedServices;
            foreach (NodeExperience item in Nodes.Where(x => x != null && x.Scope == oldScope)) item.Scope = ActiveScope;
            foreach (AccountVerificationRecord item in (AccountVerifications ?? new List<AccountVerificationRecord>())
                .Where(x => x != null && x.Scope == oldScope)) item.Scope = ActiveScope;
            if (Assurance != null && Assurance.Scope == oldScope) Assurance.Scope = ActiveScope;
        }
        else if (!preserve)
            ActiveScope = (fingerprint ?? "") + "|" + normalizedServices;
        ScopeContinuityReason = legacyServiceRemoval && preserve ? "legacy-service-migration" : preserve ? (exact ? "same-candidates" : "compatible-update") :
            (previous.Count == 0 || String.IsNullOrEmpty(previousServices) ? "initial" :
            (sameServices ? "different-subscription" : "services-changed"));
        ActiveCandidateNames = current;
        ActiveServicesKey = normalizedServices;
        return ActiveScope;
    }

    private static string RemoveLegacyJmComic(string servicesKey)
    {
        return String.Join(",", (servicesKey ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !String.Equals(x.Trim(), "JMComicWeb", StringComparison.Ordinal)).Select(x => x.Trim()));
    }

    public void Observe(string scope, CandidateScanResult scan, QualitySample quality, DateTime now)
    {
        ActiveScope = scope;
        if (scan.Health == CandidateHealth.Unknown) return;
        var node = Nodes.FirstOrDefault(x => x.Scope == scope && x.Node == scan.Name);
        if (node == null) { node = new NodeExperience { Scope = scope, Node = scan.Name, FirstUtc = now, Success = 0.5,
            RecentResponseMilliseconds = new List<double>() }; Nodes.Add(node); }
        if (node.RecentResponseMilliseconds == null) node.RecentResponseMilliseconds = new List<double>();
        if (quality != null && quality.ThroughputBytesPerSecond > 0 && quality.CheckedUtc > node.SpeedUtc)
        { node.Throughput = quality.ThroughputBytesPerSecond; node.SpeedUtc = quality.CheckedUtc; }
        if (node.Samples > 0 && now - node.LastUtc > TimeSpan.FromDays(7))
        {
            node.Samples = 0; node.FirstUtc = now; node.Success = 0.5; node.ResponseMs = 0; node.JitterMs = 0;
            node.OutcomeSamples = 0; node.SuccessfulSamples = 0; node.FirstOutcomeUtc = DateTime.MinValue; node.LastOutcomeUtc = DateTime.MinValue;
            node.RecentResponseMilliseconds.Clear();
        }
        if (node.Samples > 0 && now - node.LastUtc < TimeSpan.FromSeconds(30)) return;
        bool passed = scan.Health == CandidateHealth.Compatible;
        double weight = node.Samples == 0 ? 1 : 0.2;
        node.Success = (1 - weight) * node.Success + weight * (passed ? 1 : 0);
        double response = QualityMeasurement.ResponseMilliseconds(scan, 5000);
        node.JitterMs = (1 - weight) * node.JitterMs + weight * (node.Samples == 0 ? 0 : Math.Abs(response - node.ResponseMs));
        node.ResponseMs = (1 - weight) * node.ResponseMs + weight * response;
        node.RecentResponseMilliseconds.Add(response);
        if (node.RecentResponseMilliseconds.Count > 20)
            node.RecentResponseMilliseconds.RemoveRange(0, node.RecentResponseMilliseconds.Count - 20);
        node.LastUtc = now; node.Samples = Math.Min(1000000, node.Samples + 1); node.LastPassed = passed;
        if (node.OutcomeSamples == 0) node.FirstOutcomeUtc = now;
        node.LastOutcomeUtc = now;
        node.OutcomeSamples = Math.Min(1000000, node.OutcomeSamples + 1);
        if (passed) node.SuccessfulSamples = Math.Min(1000000, node.SuccessfulSamples + 1);
    }

    public IEnumerable<NodeExperience> Recommend(string scope, IEnumerable<string> eligible, DateTime now)
    {
        var allowed = new HashSet<string>(eligible, StringComparer.Ordinal);
        return Nodes.Where(x => x.Scope == scope && allowed.Contains(x.Node) && x.Samples >= 3 &&
            x.LastUtc - x.FirstUtc >= TimeSpan.FromMinutes(10) && x.LastUtc >= now.AddDays(-7) && x.LastPassed && x.Success >= 0.8)
            .OrderByDescending(x => x.Rank(now)).ToList();
    }

    public bool IsProvenStable(string scope, string node, DateTime now)
    {
        NodeExperience experience = Nodes.FirstOrDefault(x => x.Scope == scope && x.Node == node);
        return experience != null && experience.OutcomeSamples >= 5 &&
            experience.LastOutcomeUtc - experience.FirstOutcomeUtc >= TimeSpan.FromMinutes(30) &&
            experience.LastOutcomeUtc >= now.AddDays(-7) && experience.LastPassed &&
            (double)experience.SuccessfulSamples / experience.OutcomeSamples >= 0.95;
    }

    public IList<double> RecentResponses(string scope, string node, int maximum)
    {
        NodeExperience experience = Nodes.FirstOrDefault(x => x.Scope == scope && x.Node == node);
        if (experience == null || experience.RecentResponseMilliseconds == null || maximum <= 0)
            return new List<double>();
        int skip = Math.Max(0, experience.RecentResponseMilliseconds.Count - maximum);
        return experience.RecentResponseMilliseconds.Skip(skip).ToList();
    }

    public string StabilitySummary(string scope, string node, DateTime now)
    {
        NodeExperience experience = Nodes.FirstOrDefault(x => x.Scope == scope && x.Node == node);
        if (experience == null || experience.OutcomeSamples == 0) return "稳定历史：0/5 次 · 0/30 分钟 · 0%";
        int minutes = (int)Math.Max(0, Math.Floor((experience.LastOutcomeUtc - experience.FirstOutcomeUtc).TotalMinutes));
        double rate = (double)experience.SuccessfulSamples / experience.OutcomeSamples;
        return "稳定历史：" + experience.OutcomeSamples + "/5 次 · " + minutes + "/30 分钟 · " + rate.ToString("P0");
    }

    public void RecordChange(string from, string to, string reason, DateTime now)
    {
        LastNode = to;
        if (String.IsNullOrEmpty(from) || String.IsNullOrEmpty(to) || from == to) return;
        Changes.Add(new NodeChange { Utc = now, From = from, To = to, Reason = reason });
    }

    public string LatestSelectionReason(string node)
    {
        NodeChange change = Changes.Where(x => x.To == node).OrderByDescending(x => x.Utc).FirstOrDefault();
        return change == null ? "启动时沿用 Clash 当前选择" : change.Reason;
    }

    public string SelectionSummary(string node, bool carriedOnStartup)
    {
        string reason = LatestSelectionReason(node);
        return carriedOnStartup && !reason.StartsWith("启动时沿用", StringComparison.Ordinal)
            ? "开机沿用 Clash 上次节点；上次记录：" + reason : reason;
    }
}

public sealed class ExperienceStore
{
    private readonly string path;
    public ExperienceStore(string path) { this.path = path; }
    public ExperienceData Load()
    {
        if (!File.Exists(path)) return new ExperienceData();
        try
        {
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException();
            var data = new JavaScriptSerializer().Deserialize<ExperienceData>(File.ReadAllText(path));
            if (data == null || data.Nodes == null || data.Changes == null || data.Nodes.Any(x => x == null) || data.Changes.Any(x => x == null)) throw new InvalidDataException();
            if (data.ActiveCandidateNames == null) data.ActiveCandidateNames = new List<string>();
            if (data.ServiceIncidents == null) data.ServiceIncidents = new List<ServiceIncidentRecord>();
            if (data.AccountVerifications == null) data.AccountVerifications = new List<AccountVerificationRecord>();
            return data;
        }
        catch (Exception ex)
        {
            if (!(ex is ArgumentException || ex is InvalidOperationException || ex is InvalidDataException)) throw;
            string backup = path + ".corrupt";
            File.Copy(path, backup, true);
            return new ExperienceData();
        }
    }
    public void Save(ExperienceData data, DateTime now)
    {
        data.Nodes = data.Nodes.Where(x => x.LastUtc >= now.AddDays(-30)).OrderByDescending(x => x.LastUtc).Take(256).ToList();
        data.Changes = data.Changes.OrderByDescending(x => x.Utc).Take(300).OrderBy(x => x.Utc).ToList();
        data.ServiceIncidents = (data.ServiceIncidents ?? new List<ServiceIncidentRecord>()).Where(x => x != null && x.UntilUtc > now)
            .GroupBy(x => x.Service).Select(x => x.OrderByDescending(y => y.UntilUtc).First()).ToList();
        data.AccountVerifications = (data.AccountVerifications ?? new List<AccountVerificationRecord>())
            .Where(x => x != null && x.VerifiedUtc > now.AddDays(-30))
            .GroupBy(x => (x.Scope ?? "") + "\n" + (x.Node ?? "") + "\n" + x.Service)
            .Select(x => x.OrderByDescending(y => y.VerifiedUtc).First())
            .OrderByDescending(x => x.VerifiedUtc).Take(256).ToList();
        StatusReport.WriteAtomic(path, new JavaScriptSerializer().Serialize(data));
    }
}
