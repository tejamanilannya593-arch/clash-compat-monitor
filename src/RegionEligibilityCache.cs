using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

public sealed class RegionEligibilityRecord
{
    public string Scope { get; set; }
    public string Node { get; set; }
    public string ExitFingerprint { get; set; }
    public string CountryCode { get; set; }
    public string PolicyVersion { get; set; }
    public DateTime CheckedUtc { get; set; }
}

public sealed class RegionEligibilityCache
{
    public RegionEligibilityCache()
    {
        Records = new List<RegionEligibilityRecord>();
    }

    public List<RegionEligibilityRecord> Records { get; set; }

    public bool TryGet(string scope, string node, DateTime now, out string countryCode, out bool supported)
    {
        RegionEligibilityRecord item = Records
            .Where(x => x.Scope == scope && x.Node == node &&
                x.PolicyVersion == AiRegionPolicy.SnapshotDate && x.CheckedUtc > now.AddHours(-24))
            .OrderByDescending(x => x.CheckedUtc)
            .FirstOrDefault();
        countryCode = item == null ? "" : item.CountryCode;
        supported = item != null && AiRegionPolicy.SupportsBoth(item.CountryCode);
        return item != null;
    }

    public void Remember(string scope, string node, string exitFingerprint, string countryCode, DateTime now)
    {
        Records.RemoveAll(x => x.Scope == scope && x.Node == node);
        Records.Add(new RegionEligibilityRecord {
            Scope = scope,
            Node = node,
            ExitFingerprint = exitFingerprint,
            CountryCode = countryCode,
            PolicyVersion = AiRegionPolicy.SnapshotDate,
            CheckedUtc = now
        });
        Records = Records.OrderByDescending(x => x.CheckedUtc).Take(256).ToList();
    }
}

public sealed class RegionEligibilityStore
{
    private readonly string path;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer();

    public RegionEligibilityStore(string path)
    {
        this.path = path;
    }

    public RegionEligibilityCache Load()
    {
        if (!File.Exists(path)) return new RegionEligibilityCache();
        try
        {
            RegionEligibilityCache cache = json.Deserialize<RegionEligibilityCache>(File.ReadAllText(path));
            if (cache == null || cache.Records == null) throw new InvalidDataException();
            cache.Records = cache.Records.Where(x => x != null)
                .OrderByDescending(x => x.CheckedUtc).Take(256).ToList();
            return cache;
        }
        catch
        {
            try { File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); }
            catch { }
            return new RegionEligibilityCache();
        }
    }

    public void Save(RegionEligibilityCache cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        cache.Records = (cache.Records ?? new List<RegionEligibilityRecord>())
            .Where(x => x != null).OrderByDescending(x => x.CheckedUtc).Take(256).ToList();
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json.Serialize(cache), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
}
