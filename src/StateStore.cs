using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

public sealed class NodeHealthRecord
{
    public NodeHealthRecord(string name, CandidateHealth health, DateTime checkedUtc, DateTime cooldownUntilUtc, bool explicitLocalExclusion)
        : this(name, "", health, checkedUtc, cooldownUntilUtc, explicitLocalExclusion)
    {
    }
    public NodeHealthRecord(string name, string nodeId, CandidateHealth health, DateTime checkedUtc,
        DateTime cooldownUntilUtc, bool explicitLocalExclusion)
    {
        Name = name;
        NodeId = nodeId ?? "";
        Health = health;
        CheckedUtc = checkedUtc;
        CooldownUntilUtc = cooldownUntilUtc;
        ExplicitLocalExclusion = explicitLocalExclusion;
    }
    public string Name { get; private set; }
    public string NodeId { get; internal set; }
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
        PreferredNode = "";
        PreferredNodeVerifiedUtc = DateTime.MinValue;
        Records = new Dictionary<string, NodeHealthRecord>(StringComparer.Ordinal);
    }
    public string SubscriptionFingerprint { get; private set; }
    public string PreferredNode { get; internal set; }
    public string PreferredNodeId { get; internal set; }
    public DateTime PreferredNodeVerifiedUtc { get; internal set; }
    public Dictionary<string, NodeHealthRecord> Records { get; private set; }

    public void RememberPreferred(string name, CandidateHealth health, DateTime verifiedUtc)
    {
        RememberPreferred(name, "", health, verifiedUtc);
    }
    public void RememberPreferred(string name, string nodeId, CandidateHealth health, DateTime verifiedUtc)
    {
        if (health != CandidateHealth.Compatible) return;
        PreferredNode = name ?? "";
        PreferredNodeId = nodeId ?? "";
        PreferredNodeVerifiedUtc = verifiedUtc.ToUniversalTime();
    }

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
            if (lines.Length == 0 || (!lines[0].StartsWith("CCM1\t", StringComparison.Ordinal) &&
                !lines[0].StartsWith("CCM2\t", StringComparison.Ordinal))) throw new InvalidDataException();
            bool hasIdentity = lines[0].StartsWith("CCM2\t", StringComparison.Ordinal);
            var state = new HealthState(lines[0].Substring(5));
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] fields = lines[i].Split('\t');
                if (((!hasIdentity && fields.Length == 3) || (hasIdentity && fields.Length == 4)) && fields[0] == "P")
                {
                    state.PreferredNode = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));
                    state.PreferredNodeId = hasIdentity ? Encoding.UTF8.GetString(Convert.FromBase64String(fields[2])) : "";
                    state.PreferredNodeVerifiedUtc = new DateTime(long.Parse(fields[hasIdentity ? 3 : 2], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                    continue;
                }
                if (((!hasIdentity && fields.Length != 6) || (hasIdentity && fields.Length != 7)) || fields[0] != "N")
                    throw new InvalidDataException();
                string name = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));
                string nodeId = hasIdentity ? Encoding.UTF8.GetString(Convert.FromBase64String(fields[2])) : "";
                int offset = hasIdentity ? 1 : 0;
                CandidateHealth health = (CandidateHealth)Enum.Parse(typeof(CandidateHealth), fields[2 + offset], false);
                DateTime checkedUtc = new DateTime(long.Parse(fields[3 + offset], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                DateTime cooldownUtc = new DateTime(long.Parse(fields[4 + offset], CultureInfo.InvariantCulture), DateTimeKind.Utc);
                state.Records[name] = new NodeHealthRecord(name, nodeId, health, checkedUtc, cooldownUtc, fields[5 + offset] == "1");
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
            writer.WriteLine("CCM2\t" + state.SubscriptionFingerprint);
            if (!String.IsNullOrEmpty(state.PreferredNode) && allowed.Contains(state.PreferredNode) && state.PreferredNodeVerifiedUtc != DateTime.MinValue)
                writer.WriteLine("P\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(state.PreferredNode)) + "\t" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(state.PreferredNodeId ?? "")) + "\t" +
                    state.PreferredNodeVerifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            foreach (NodeHealthRecord record in state.Records.Values.Where(x => allowed.Contains(x.Name)))
            {
                string line = "N\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(record.Name)) + "\t" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(record.NodeId ?? "")) + "\t" + record.Health + "\t" +
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
