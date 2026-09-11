using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public sealed class ExitIdentity
{
    public ExitIdentity(string fingerprint, string countryCode, string detail)
    {
        Fingerprint = fingerprint ?? "";
        CountryCode = countryCode ?? "";
        Detail = detail ?? "";
    }

    public string Fingerprint { get; private set; }
    public string CountryCode { get; private set; }
    public string Detail { get; private set; }
    public bool Known { get { return Fingerprint.Length > 0 && CountryCode.Length == 2; } }
}

public static class ExitIdentityParser
{
    public static ExitIdentity Parse(string trace, byte[] key)
    {
        if (key == null || key.Length == 0) return new ExitIdentity("", "", "missing fingerprint key");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (trace ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1) continue;
            values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }

        string ip;
        string country;
        if (!values.TryGetValue("ip", out ip) || String.IsNullOrWhiteSpace(ip))
            return new ExitIdentity("", "", "trace did not contain an exit address");
        if (!values.TryGetValue("loc", out country) || country == null || country.Trim().Length != 2)
            return new ExitIdentity("", "", "trace did not contain a two-letter country code");

        country = country.Trim().ToUpperInvariant();
        byte[] digest;
        using (var hmac = new HMACSHA256(key))
            digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(ip.Trim()));
        return new ExitIdentity(BitConverter.ToString(digest).Replace("-", ""), country, "ok");
    }
}

public interface IExitIdentityProbe
{
    ExitIdentity Probe(TimeSpan timeout);
}

public sealed class CloudflareExitIdentityProbe : IExitIdentityProbe
{
    private static readonly Uri Endpoint = new Uri("https://www.cloudflare.com/cdn-cgi/trace");
    private readonly string proxyUrl;
    private readonly byte[] key;

    public CloudflareExitIdentityProbe(string proxyUrl, string keyPath)
    {
        this.proxyUrl = proxyUrl;
        key = ExitIdentityKey.LoadOrCreate(keyPath);
    }

    public ExitIdentity Probe(TimeSpan timeout)
    {
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
                using (HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellation.Token).GetAwaiter().GetResult())
                {
                    if ((int)response.StatusCode != 200) return new ExitIdentity("", "", "exit trace HTTP " + (int)response.StatusCode);
                    using (Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    {
                        byte[] body = HttpServiceProbe.ReadLimitedAsync(stream, 4096, cancellation.Token).GetAwaiter().GetResult();
                        return ExitIdentityParser.Parse(Encoding.UTF8.GetString(body), key);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!(ex is HttpRequestException || ex is IOException || ex is OperationCanceledException)) throw;
            return new ExitIdentity("", "", "exit trace unavailable");
        }
    }
}

public static class ExitIdentityKey
{
    public static byte[] LoadOrCreate(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Identity key path is required.", "path");
        if (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (existing.Length == 32) return existing;
        }
        string directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var created = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(created);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, created);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
        return created;
    }
}

public static class ChatGptSupportedRegions
{
    public const string SnapshotDate = "2026-09-11";
    private static readonly HashSet<string> Codes = new HashSet<string>(
        ("AF AX AL DZ AD AO AG AR AM AW AU AT AZ BS BH BD BB BE BZ BM BJ BT BO BA BW BR BN BG BF BI " +
         "CV KH CM CA KY CF TD CL CO KM CG CD CR CI HR CY CZ DK DJ DM DO EC EG SV GQ ER EE SZ ET FO FJ " +
         "FI FR GF PF TF GA GM GE DE GH GR GD GL GT GP GN GW GY HT VA HN HU IS IN ID IQ IE IL IT JM JP " +
         "JO KZ KE KI KW KG LA LV LB LS LR LY LI LT LU MG MW MY MV ML MT MH MQ MR MU YT MX FM MD MC ME " +
         "MA MZ MM NA NR NP NL NC NZ NI NE NG MK NO OM PK PW PS PA PG PY PE PH PL PT QA RE RO RW BL SH " +
         "KN LC MF PM VC WS SM ST SA SN RS SC SL SG SK SI SB SO ZA KR SS ES LK SR SE CH SD SJ TW TJ TZ " +
         "TH TL TG TO TT TN TR TM TV UG UA AE GB US UY UZ VU VN WF YE ZM ZW")
        .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string countryCode)
    {
        return !String.IsNullOrWhiteSpace(countryCode) && Codes.Contains(countryCode.Trim());
    }
}
