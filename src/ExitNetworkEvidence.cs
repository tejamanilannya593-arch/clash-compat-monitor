using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public sealed class ExitAsnResolution
{
    public bool Matched { get; set; }
    public long Asn { get; set; }
    public string Detail { get; set; }
}

public interface IExitAsnResolver
{
    ExitAsnResolution Resolve(ExitIdentity expected, TimeSpan timeout);
}

public static class IpWhoExitAsnParser
{
    public static ExitAsnResolution Parse(string body, byte[] key,
        string expectedFingerprint, string expectedCountryCode)
    {
        try
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024, RecursionLimit = 8 };
            var root = json.Deserialize<Dictionary<string, object>>(body ?? "");
            object successValue;
            object ipValue;
            object countryValue;
            object connectionValue;
            if (root == null || !root.TryGetValue("success", out successValue) ||
                !(successValue is bool) || !(bool)successValue ||
                !root.TryGetValue("ip", out ipValue) ||
                !root.TryGetValue("country_code", out countryValue) ||
                !root.TryGetValue("connection", out connectionValue))
                return Unavailable("provider-response-invalid");
            string fingerprint = ExitIdentityKey.Fingerprint(Convert.ToString(ipValue), key);
            string country = (Convert.ToString(countryValue) ?? "").Trim().ToUpperInvariant();
            if (!String.Equals(fingerprint, expectedFingerprint, StringComparison.Ordinal))
                return Unavailable("fingerprint-mismatch");
            if (!String.Equals(country, (expectedCountryCode ?? "").Trim().ToUpperInvariant(),
                StringComparison.Ordinal)) return Unavailable("country-mismatch");
            var connection = connectionValue as Dictionary<string, object>;
            object asnValue;
            long asn;
            if (connection == null || !connection.TryGetValue("asn", out asnValue) ||
                !Int64.TryParse(Convert.ToString(asnValue,
                    System.Globalization.CultureInfo.InvariantCulture), out asn) || asn <= 0)
                return Unavailable("asn-invalid");
            return new ExitAsnResolution { Matched = true, Asn = asn, Detail = "ok" };
        }
        catch (Exception ex)
        {
            if (!(ex is ArgumentException || ex is InvalidOperationException ||
                ex is FormatException || ex is OverflowException)) throw;
            return Unavailable("provider-response-invalid");
        }
    }

    private static ExitAsnResolution Unavailable(string detail)
    {
        return new ExitAsnResolution { Matched = false, Asn = 0, Detail = detail };
    }
}

public sealed class IpWhoExitAsnResolver : IExitAsnResolver
{
    private static readonly Uri Endpoint = new Uri("https://ipwho.is/");
    private readonly string proxyUrl;
    private readonly byte[] key;

    public IpWhoExitAsnResolver(string proxyUrl, byte[] key)
    {
        this.proxyUrl = proxyUrl;
        this.key = key;
    }

    public ExitAsnResolution Resolve(ExitIdentity expected, TimeSpan timeout)
    {
        if (expected == null || !expected.Known)
            return new ExitAsnResolution { Matched = false, Detail = "cloudflare-identity-unknown" };
        try
        {
            var handler = new HttpClientHandler { Proxy = new WebProxy(proxyUrl), UseProxy = true,
                UseCookies = false, AllowAutoRedirect = false };
            using (handler)
            using (var client = new HttpClient(handler))
            using (var cancellation = new CancellationTokenSource(timeout))
            using (var request = new HttpRequestMessage(HttpMethod.Get, Endpoint))
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ClashCompatibilityMonitor/1.0");
                using (HttpResponseMessage response = client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellation.Token).GetAwaiter().GetResult())
                {
                    if ((int)response.StatusCode != 200)
                        return new ExitAsnResolution { Matched = false, Detail = "provider-http-" + (int)response.StatusCode };
                    using (Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    {
                        byte[] bytes = HttpServiceProbe.ReadLimitedAsync(stream, 16 * 1024,
                            cancellation.Token).GetAwaiter().GetResult();
                        return IpWhoExitAsnParser.Parse(Encoding.UTF8.GetString(bytes), key,
                            expected.Fingerprint, expected.CountryCode);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!(ex is HttpRequestException || ex is IOException ||
                ex is OperationCanceledException || ex is ArgumentException)) throw;
            return new ExitAsnResolution { Matched = false, Detail = "provider-unavailable" };
        }
    }
}

public sealed class ExitNetworkEvidenceRecord
{
    public string ExitFingerprint { get; set; }
    public string CountryCode { get; set; }
    public long Asn { get; set; }
    public DateTime ObservedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

public sealed class ExitNetworkEvidenceCache
{
    public ExitNetworkEvidenceCache()
    {
        Records = new List<ExitNetworkEvidenceRecord>();
    }

    public List<ExitNetworkEvidenceRecord> Records { get; set; }

    public bool TryGet(string fingerprint, string countryCode, DateTime nowUtc, out long asn)
    {
        ExitNetworkEvidenceRecord record = (Records ?? new List<ExitNetworkEvidenceRecord>())
            .Where(x => IsValidRecord(x, nowUtc, nowUtc.AddMinutes(5)) &&
                String.Equals(x.ExitFingerprint, fingerprint, StringComparison.Ordinal) &&
                String.Equals(x.CountryCode, countryCode, StringComparison.Ordinal))
            .OrderByDescending(x => x.ObservedUtc).FirstOrDefault();
        asn = record == null ? 0 : record.Asn;
        return record != null;
    }

    public void Remember(string fingerprint, string countryCode, long asn,
        DateTime observedUtc, TimeSpan lifetime)
    {
        var record = new ExitNetworkEvidenceRecord { ExitFingerprint = fingerprint,
            CountryCode = countryCode, Asn = asn, ObservedUtc = observedUtc,
            ExpiresUtc = observedUtc.Add(lifetime) };
        if (!IsValidRecord(record, observedUtc, observedUtc.AddMinutes(5)) ||
            lifetime != TimeSpan.FromMinutes(60))
            throw new ArgumentException("Canonical exit evidence with a sixty-minute lifetime is required.");
        if (Records == null) Records = new List<ExitNetworkEvidenceRecord>();
        Records.RemoveAll(x => x != null &&
            String.Equals(x.ExitFingerprint, fingerprint, StringComparison.Ordinal));
        Records.Add(record);
        Records = Records.OrderByDescending(x => x.ObservedUtc).Take(256).ToList();
    }

    internal static bool IsValidRecord(ExitNetworkEvidenceRecord record,
        DateTime nowUtc, DateTime latestAllowedUtc)
    {
        return record != null && IsFingerprint(record.ExitFingerprint) &&
            IsCountry(record.CountryCode) && record.Asn > 0 &&
            record.ObservedUtc != DateTime.MinValue && record.ObservedUtc <= latestAllowedUtc &&
            record.ExpiresUtc > nowUtc && record.ExpiresUtc <= record.ObservedUtc.AddMinutes(60);
    }

    private static bool IsCountry(string value)
    {
        return value != null && value.Length == 2 && value[0] >= 'A' && value[0] <= 'Z' &&
            value[1] >= 'A' && value[1] <= 'Z';
    }

    private static bool IsFingerprint(string value)
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

public sealed class ExitNetworkEvidenceStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private readonly string path;
    private readonly string mutexName;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer {
        MaxJsonLength = MaximumFileBytes, RecursionLimit = 16
    };

    public ExitNetworkEvidenceStore(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An exit evidence path is required.", "path");
        this.path = Path.GetFullPath(path);
        mutexName = MutexName(this.path);
    }

    public ExitNetworkEvidenceCache Load(DateTime nowUtc)
    {
        return WithLock(delegate {
            if (!File.Exists(path)) return new ExitNetworkEvidenceCache();
            try
            {
                if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException();
                ExitNetworkEvidenceCache cache = json.Deserialize<ExitNetworkEvidenceCache>(File.ReadAllText(path));
                if (cache == null || cache.Records == null) throw new InvalidDataException();
                cache.Records = cache.Records.Where(x => ExitNetworkEvidenceCache.IsValidRecord(
                    x, nowUtc, nowUtc.AddMinutes(5))).GroupBy(x => x.ExitFingerprint,
                    StringComparer.Ordinal).Select(x => x.OrderByDescending(y => y.ObservedUtc).First())
                    .OrderByDescending(x => x.ObservedUtc).Take(256).ToList();
                return cache;
            }
            catch
            {
                ArchiveCorrupt();
                return new ExitNetworkEvidenceCache();
            }
        });
    }

    public void Save(ExitNetworkEvidenceCache cache, DateTime nowUtc)
    {
        if (cache == null) throw new ArgumentNullException("cache");
        WithLock(delegate {
            cache.Records = (cache.Records ?? new List<ExitNetworkEvidenceRecord>())
                .Where(x => ExitNetworkEvidenceCache.IsValidRecord(x, nowUtc, nowUtc.AddMinutes(5)))
                .GroupBy(x => x.ExitFingerprint, StringComparer.Ordinal)
                .Select(x => x.OrderByDescending(y => y.ObservedUtc).First())
                .OrderByDescending(x => x.ObservedUtc).Take(256).ToList();
            if (cache.Records.Count == 0 && !File.Exists(path)) return;
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
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
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }

    private void WithLock(Action action)
    {
        WithLock<object>(delegate { action(); return null; });
    }

    private static string MutexName(string normalizedPath)
    {
        using (SHA256 hash = SHA256.Create())
        {
            byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant()));
            return "Local\\ClashCompatibilityMonitor.ExitNetworkEvidence." +
                BitConverter.ToString(digest).Replace("-", "");
        }
    }
}

public sealed class ExitNetworkEvidenceEnricher
{
    private readonly IExitAsnResolver resolver;
    private readonly ExitNetworkEvidenceStore store;

    public ExitNetworkEvidenceEnricher(IExitAsnResolver resolver, ExitNetworkEvidenceStore store)
    {
        this.resolver = resolver;
        this.store = store;
    }

    public ExitIdentity Enrich(ExitIdentity identity, DateTime nowUtc, TimeSpan timeout)
    {
        if (identity == null || !identity.Known || resolver == null || store == null) return identity;
        try
        {
            ExitNetworkEvidenceCache cache = store.Load(nowUtc);
            long cachedAsn;
            if (cache.TryGet(identity.Fingerprint, identity.CountryCode, nowUtc, out cachedAsn))
                return identity.WithAsn(cachedAsn, nowUtc);
            ExitAsnResolution resolution = resolver.Resolve(identity, timeout);
            if (resolution == null || !resolution.Matched || resolution.Asn <= 0) return identity;
            cache.Remember(identity.Fingerprint, identity.CountryCode, resolution.Asn,
                nowUtc, TimeSpan.FromMinutes(60));
            store.Save(cache, nowUtc);
            return identity.WithAsn(resolution.Asn, nowUtc);
        }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is InvalidOperationException)) throw;
            return identity;
        }
    }
}
