using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

public sealed class QualitySample
{
    public QualitySample(string name, DateTime checkedUtc, bool compatible, double responseMedianMs,
        double jitterMs, double throughputBytesPerSecond, double? multiplier)
    {
        Name = name;
        CheckedUtc = checkedUtc;
        Compatible = compatible;
        ResponseMedianMs = responseMedianMs;
        JitterMs = jitterMs;
        ThroughputBytesPerSecond = throughputBytesPerSecond;
        Multiplier = multiplier;
    }
    public string Name { get; private set; }
    public DateTime CheckedUtc { get; private set; }
    public bool Compatible { get; private set; }
    public double ResponseMedianMs { get; private set; }
    public double JitterMs { get; private set; }
    public double ThroughputBytesPerSecond { get; private set; }
    public double? Multiplier { get; private set; }
}

public sealed class QualityBreakdown
{
    public double StabilityWeight { get { return 40.0; } }
    public double ResponsivenessWeight { get { return 25.0; } }
    public double ThroughputWeight { get { return 25.0; } }
    public double CostWeight { get { return 10.0; } }
    public double Stability { get; set; }
    public double Responsiveness { get; set; }
    public double Throughput { get; set; }
    public double Cost { get; set; }
    public double Score { get; set; }
}

public static class QualityScorer
{
    private static readonly Regex MultiplierPattern = new Regex(
        @"(\d+(?:\.\d+)?)\s*(?:x|倍)(?:\b|\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MultiplierPrefixPattern = new Regex(
        @"倍率\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static double? ParseMultiplier(string name)
    {
        Match match = MultiplierPattern.Match(name ?? "");
        if (!match.Success) match = MultiplierPrefixPattern.Match(name ?? "");
        double value;
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value) && value > 0 ? (double?)value : null;
    }

    public static QualityBreakdown Score(QualitySample sample, IEnumerable<QualitySample> nodeHistory,
        IEnumerable<QualitySample> cohort, DateTime nowUtc)
    {
        var history = (nodeHistory ?? Enumerable.Empty<QualitySample>()).
            Where(x => x.Name == sample.Name && x.CheckedUtc >= nowUtc.AddMinutes(-120)).OrderBy(x => x.CheckedUtc).ToList();
        if (history.Count == 0) history.Add(sample);
        var peers = (cohort ?? Enumerable.Empty<QualitySample>()).Where(x => x.Compatible).ToList();
        if (!peers.Contains(sample) && sample.Compatible) peers.Add(sample);

        double recent30 = SuccessRate(history.Where(x => x.CheckedUtc >= nowUtc.AddMinutes(-30)));
        double recent120 = SuccessRate(history);
        double stability = 100.0 * (0.7 * recent30 + 0.3 * recent120);
        int consecutiveFailures = 0;
        foreach (QualitySample item in history.OrderByDescending(x => x.CheckedUtc))
        {
            if (item.Compatible) break;
            consecutiveFailures++;
        }
        stability = Clamp(stability - 15.0 * consecutiveFailures);

        double latency = LowerIsBetter(sample.ResponseMedianMs, peers.Select(x => x.ResponseMedianMs));
        double jitter = LowerIsBetter(sample.JitterMs, peers.Select(x => x.JitterMs));
        double responsiveness = Clamp(0.75 * latency + 0.25 * jitter);
        double throughput = HigherIsBetter(sample.ThroughputBytesPerSecond, peers.Select(x => x.ThroughputBytesPerSecond));

        var known = peers.Select(x => x.Multiplier ?? ParseMultiplier(x.Name)).Where(x => x.HasValue).Select(x => x.Value).OrderBy(x => x).ToList();
        double fallback = known.Count == 0 ? 1.0 : Median(known);
        double ownMultiplier = sample.Multiplier ?? ParseMultiplier(sample.Name) ?? fallback;
        var resolved = peers.Select(x => x.Multiplier ?? ParseMultiplier(x.Name) ?? fallback);
        double cost = LowerIsBetter(ownMultiplier, resolved);

        return new QualityBreakdown {
            Stability = stability,
            Responsiveness = responsiveness,
            Throughput = throughput,
            Cost = cost,
            Score = Clamp(stability * 0.40 + responsiveness * 0.25 + throughput * 0.25 + cost * 0.10)
        };
    }

    private static double SuccessRate(IEnumerable<QualitySample> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : (double)list.Count(x => x.Compatible) / list.Count;
    }

    private static double LowerIsBetter(double value, IEnumerable<double> values)
    {
        var list = values.Where(IsFinite).ToList();
        if (list.Count == 0 || !IsFinite(value)) return 0;
        double min = list.Min(), max = list.Max();
        return Math.Abs(max - min) < 0.000001 ? 50 : Clamp(100.0 * (max - value) / (max - min));
    }

    private static double HigherIsBetter(double value, IEnumerable<double> values)
    {
        var list = values.Where(IsFinite).ToList();
        if (list.Count == 0 || !IsFinite(value)) return 0;
        double min = list.Min(), max = list.Max();
        return Math.Abs(max - min) < 0.000001 ? 50 : Clamp(100.0 * (value - min) / (max - min));
    }

    private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0; }
    private static double Median(IList<double> sorted) { return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0; }
    private static double Clamp(double value) { return Math.Max(0, Math.Min(100, value)); }
}

public sealed class QualityStateStore
{
    private readonly string path;
    public QualityStateStore(string path) { this.path = path; }

    public List<QualitySample> Load()
    {
        if (!File.Exists(path)) return new List<QualitySample>();
        try
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0 || lines[0] != "CQM1") throw new InvalidDataException();
            var result = new List<QualitySample>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                if (f.Length != 8 || f[0] != "Q") throw new InvalidDataException();
                string name = Encoding.UTF8.GetString(Convert.FromBase64String(f[1]));
                double? multiplier = f[7].Length == 0 ? (double?)null : ParseDouble(f[7]);
                result.Add(new QualitySample(name, new DateTime(ParseLong(f[2]), DateTimeKind.Utc), f[3] == "1",
                    ParseDouble(f[4]), ParseDouble(f[5]), ParseDouble(f[6]), multiplier));
            }
            return result;
        }
        catch
        {
            string corrupt = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            try { File.Move(path, corrupt); } catch { }
            return new List<QualitySample>();
        }
    }

    public void Save(IEnumerable<QualitySample> samples)
    {
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        var bounded = Bound(samples).OrderByDescending(x => x.CheckedUtc).ToList();
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.WriteLine("CQM1");
            foreach (QualitySample sample in bounded)
            {
                string line = "Q\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(sample.Name)) + "\t" +
                    sample.CheckedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" + (sample.Compatible ? "1" : "0") + "\t" +
                    Format(sample.ResponseMedianMs) + "\t" + Format(sample.JitterMs) + "\t" + Format(sample.ThroughputBytesPerSecond) + "\t" +
                    (sample.Multiplier.HasValue ? Format(sample.Multiplier.Value) : "");
                writer.Flush();
                if (stream.Length + Encoding.UTF8.GetByteCount(line) + 2 > 256 * 1024) break;
                writer.WriteLine(line);
            }
            writer.Flush();
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }

    public static List<QualitySample> Bound(IEnumerable<QualitySample> samples)
    {
        return (samples ?? Enumerable.Empty<QualitySample>()).GroupBy(x => x.Name, StringComparer.Ordinal)
            .SelectMany(g => g.OrderByDescending(x => x.CheckedUtc).Take(120)).ToList();
    }

    private static string Format(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    private static double ParseDouble(string value) { return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture); }
    private static long ParseLong(string value) { return long.Parse(value, CultureInfo.InvariantCulture); }
}

public static class CandidatePreselector
{
    public static IList<CandidateNode> Select(IEnumerable<CandidateNode> candidates, IDictionary<string, int> delays,
        string currentName, int maximum)
    {
        var ordered = (candidates ?? Enumerable.Empty<CandidateNode>()).OrderBy(x => {
            int delay;
            return delays != null && delays.TryGetValue(x.Name, out delay) ? delay : Int32.MaxValue;
        }).Take(Math.Max(0, maximum)).ToList();
        CandidateNode current = (candidates ?? Enumerable.Empty<CandidateNode>()).FirstOrDefault(x => x.Name == currentName);
        if (current != null && maximum > 0 && !ordered.Any(x => x.Name == currentName))
        {
            if (ordered.Count == maximum) ordered.RemoveAt(ordered.Count - 1);
            ordered.Add(current);
        }
        return ordered;
    }
}

public static class RefreshPolicy
{
    public static bool ShouldRefreshCandidates(bool subscriptionChanged, CandidateHealth health, bool scheduled)
    {
        return subscriptionChanged || (health != CandidateHealth.Compatible && health != CandidateHealth.BasicCompatible && health != CandidateHealth.Unknown) || scheduled;
    }
    public static bool ShouldRunThroughput(bool subscriptionChanged, CandidateHealth currentHealth,
        DateTime lastThroughputUtc, DateTime nowUtc, TimeSpan interval)
    {
        return subscriptionChanged || nowUtc - lastThroughputUtc >= interval;
    }
}
