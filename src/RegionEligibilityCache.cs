using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
    private const int MaxScopeLength = 256;
    private const int MaxNodeLength = 512;
    private const int MaxPolicyVersionLength = 32;

    public RegionEligibilityCache()
    {
        Records = new List<RegionEligibilityRecord>();
    }

    public List<RegionEligibilityRecord> Records { get; set; }

    public bool TryGet(string scope, string node, string expectedExitFingerprint, DateTime now,
        out string countryCode, out bool supported)
    {
        RegionEligibilityRecord item = (Records ?? new List<RegionEligibilityRecord>())
            .Where(x => IsValidRecord(x, now.AddMinutes(5)) && x.Scope == scope && x.Node == node &&
                String.Equals(x.ExitFingerprint, expectedExitFingerprint, StringComparison.Ordinal) &&
                x.PolicyVersion == AiRegionPolicy.SnapshotDate && x.CheckedUtc > now.AddHours(-24))
            .OrderByDescending(x => x.CheckedUtc)
            .FirstOrDefault();
        countryCode = item == null ? "" : item.CountryCode;
        supported = item != null && AiRegionPolicy.SupportsBoth(item.CountryCode);
        return item != null;
    }

    public void Remember(string scope, string node, string exitFingerprint, string countryCode, DateTime now)
    {
        if (!IsBoundedText(scope, MaxScopeLength))
            throw new ArgumentException("A bounded cache scope is required.", "scope");
        if (!IsBoundedText(node, MaxNodeLength))
            throw new ArgumentException("A bounded node name is required.", "node");
        if (!IsCanonicalFingerprint(exitFingerprint))
            throw new ArgumentException("A canonical hashed exit fingerprint is required.", "exitFingerprint");
        if (!IsCountryCode(countryCode))
            throw new ArgumentException("A two-letter country code is required.", "countryCode");
        if (Records == null) Records = new List<RegionEligibilityRecord>();
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

    public bool TryRemember(string scope, string node, string exitFingerprint, string countryCode, DateTime now)
    {
        if (!CanRemember(scope, node, exitFingerprint, countryCode)) return false;
        Remember(scope, node, exitFingerprint, countryCode, now);
        return true;
    }

    public static bool CanRemember(string scope, string node, string exitFingerprint, string countryCode)
    {
        return IsBoundedText(scope, MaxScopeLength) && IsBoundedText(node, MaxNodeLength) &&
            IsCanonicalFingerprint(exitFingerprint) && IsCountryCode(countryCode);
    }

    internal static bool IsValidRecord(RegionEligibilityRecord item, DateTime latestAllowedUtc)
    {
        return item != null && IsBoundedText(item.Scope, MaxScopeLength) &&
            IsBoundedText(item.Node, MaxNodeLength) && IsCanonicalFingerprint(item.ExitFingerprint) &&
            IsCountryCode(item.CountryCode) && IsBoundedText(item.PolicyVersion, MaxPolicyVersionLength) &&
            item.CheckedUtc != DateTime.MinValue && item.CheckedUtc <= latestAllowedUtc;
    }

    private static bool IsBoundedText(string value, int maximumLength)
    {
        return !String.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;
    }

    private static bool IsCountryCode(string value)
    {
        return value != null && value.Length == 2 && value[0] >= 'A' && value[0] <= 'Z' &&
            value[1] >= 'A' && value[1] <= 'Z';
    }

    private static bool IsCanonicalFingerprint(string value)
    {
        if (value == null || value.Length != 64) return false;
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (!((current >= '0' && current <= '9') || (current >= 'A' && current <= 'F'))) return false;
        }
        return true;
    }
}

public sealed class RegionEligibilityStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private readonly string path;
    private readonly string mutexName;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer {
        MaxJsonLength = MaximumFileBytes,
        RecursionLimit = 16
    };

    public RegionEligibilityStore(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A region eligibility path is required.", "path");
        this.path = Path.GetFullPath(path);
        mutexName = MutexName(this.path);
    }

    public RegionEligibilityCache Load()
    {
        return WithLock(delegate {
            if (!File.Exists(path)) return new RegionEligibilityCache();
            try
            {
                if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException();
                RegionEligibilityCache cache = json.Deserialize<RegionEligibilityCache>(File.ReadAllText(path));
                if (cache == null || cache.Records == null) throw new InvalidDataException();
                DateTime latestAllowedUtc = DateTime.UtcNow.AddMinutes(5);
                cache.Records = cache.Records.Where(x => RegionEligibilityCache.IsValidRecord(x, latestAllowedUtc))
                    .OrderByDescending(x => x.CheckedUtc).Take(256).ToList();
                return cache;
            }
            catch
            {
                ArchiveCorrupt();
                return new RegionEligibilityCache();
            }
        });
    }

    public void Save(RegionEligibilityCache cache)
    {
        if (cache == null) throw new ArgumentNullException("cache");
        WithLock(delegate {
            DateTime latestAllowedUtc = DateTime.UtcNow.AddMinutes(5);
            cache.Records = (cache.Records ?? new List<RegionEligibilityRecord>())
                .Where(x => RegionEligibilityCache.IsValidRecord(x, latestAllowedUtc))
                .OrderByDescending(x => x.CheckedUtc).Take(256).ToList();
            if (cache.Records.Count == 0 && !File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, json.Serialize(cache), new UTF8Encoding(false));
                ReplaceFile(temporary);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        });
    }

    private void ReplaceFile(string temporary)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return;
            }
            catch (IOException)
            {
                if (attempt >= 4) throw;
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    private void ArchiveCorrupt()
    {
        if (!File.Exists(path)) return;
        string archive = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") +
            "-" + Guid.NewGuid().ToString("N");
        try { File.Move(path, archive); }
        catch
        {
            try { File.Delete(path); }
            catch { }
        }
    }

    private T WithLock<T>(Func<T> action)
    {
        using (var mutex = new Mutex(false, mutexName))
        {
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(); }
                catch (AbandonedMutexException) { acquired = true; }
                return action();
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
    }

    private void WithLock(Action action)
    {
        WithLock<object>(delegate { action(); return null; });
    }

    private static string MutexName(string normalizedPath)
    {
        string identity = normalizedPath.ToUpperInvariant();
        using (SHA256 hash = SHA256.Create())
        {
            byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(identity));
            return "Local\\ClashCompatibilityMonitor.RegionEligibility." +
                BitConverter.ToString(digest).Replace("-", "");
        }
    }
}
