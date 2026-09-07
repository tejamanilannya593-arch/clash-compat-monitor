using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public sealed class NodeHealthRecord
{
    public NodeHealthRecord(string name, CandidateHealth health, DateTime checkedUtc, DateTime cooldownUntilUtc, bool explicitLocalExclusion)
    {
        Name = name;
        Health = health;
        CheckedUtc = checkedUtc;
        CooldownUntilUtc = cooldownUntilUtc;
        ExplicitLocalExclusion = explicitLocalExclusion;
    }
    public string Name { get; private set; }
    public CandidateHealth Health { get; set; }
    public DateTime CheckedUtc { get; private set; }
    public DateTime CooldownUntilUtc { get; private set; }
    public bool ExplicitLocalExclusion { get; private set; }
}

public sealed class HealthState
{
    public HealthState(string subscriptionFingerprint)
    {
        SubscriptionFingerprint = subscriptionFingerprint ?? "";
        Records = new Dictionary<string, NodeHealthRecord>(StringComparer.Ordinal);
    }
    public string SubscriptionFingerprint { get; private set; }
    public Dictionary<string, NodeHealthRecord> Records { get; private set; }

    public void ApplySubscriptionFingerprint(string fingerprint)
    {
        if (string.Equals(SubscriptionFingerprint, fingerprint, StringComparison.Ordinal)) return;
        foreach (NodeHealthRecord record in Records.Values)
            if (!record.ExplicitLocalExclusion) record.Health = CandidateHealth.Unknown;
        SubscriptionFingerprint = fingerprint ?? "";
    }
}

public sealed class StateStore
{
    private readonly string path;
    public StateStore(string path) { this.path = path; }

    public HealthState Load()
    {
        if (!File.Exists(path)) return new HealthState("");
        try
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0 || !lines[0].StartsWith("CCM1\t", StringComparison.Ordinal)) throw new InvalidDataException();
            var state = new HealthState(lines[0].Substring(5));
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] fields = lines[i].Split('\t');
                if (fields.Length != 6 || fields[0] != "N") throw new InvalidDataException();
                string name = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));
                CandidateHealth health = (CandidateHealth)Enum.Parse(typeof(CandidateHealth), fields[2], false);
                DateTime checkedUtc = new DateTime(long.Parse(fields[3], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                DateTime cooldownUtc = new DateTime(long.Parse(fields[4], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                state.Records[name] = new NodeHealthRecord(name, health, checkedUtc, cooldownUtc, fields[5] == "1");
            }
            return state;
        }
        catch
        {
            string corrupt = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            try { File.Move(path, corrupt); } catch { }
            return new HealthState("");
        }
    }

    public void Save(HealthState state, IEnumerable<string> currentCandidates)
    {
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        var allowed = new HashSet<string>(currentCandidates ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.WriteLine("CCM1\t" + state.SubscriptionFingerprint);
            foreach (NodeHealthRecord record in state.Records.Values.Where(x => allowed.Contains(x.Name)))
            {
                string line = "N\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(record.Name)) + "\t" + record.Health + "\t" +
                    record.CheckedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" + record.CooldownUntilUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" + (record.ExplicitLocalExclusion ? "1" : "0");
                writer.Flush();
                if (stream.Length + Encoding.UTF8.GetByteCount(line) + 2 > 256 * 1024) break;
                writer.WriteLine(line);
            }
            writer.Flush();
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
