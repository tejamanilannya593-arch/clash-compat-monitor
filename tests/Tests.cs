using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows.Forms;

internal static class Tests
{
    private static int failures;

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!object.Equals(expected, actual))
        {
            failures++;
            Console.Error.WriteLine("FAIL " + name + ": expected=" + expected + " actual=" + actual);
        }
        else
        {
            Console.WriteLine("PASS " + name);
        }
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
            Equal(true, false, name);
        }
        catch (T)
        {
            Equal(true, true, name);
        }
    }

    private static void DeleteDirectoryEventually(string path)
    {
        IOException lastFailure = null;
        bool deleted = SpinWait.SpinUntil(delegate {
            if (!Directory.Exists(path)) return true;
            try
            {
                Directory.Delete(path, true);
                return !Directory.Exists(path);
            }
            catch (IOException ex)
            {
                lastFailure = ex;
                return false;
            }
        }, TimeSpan.FromSeconds(2));
        if (!deleted)
            throw lastFailure ?? new IOException("Timed out while deleting test directory: " + path);
    }

    public static int Main()
    {
        Equal("ClashCompatibilityMonitor", MonitorIdentity.Name, "identity");
        Equal("0.7.0-preview.8", MonitorIdentity.Version, "release version");
        Equal(TimeSpan.FromMinutes(30), MonitorConfiguration.CreateDefault().ReloadRecoveryFreshness, "reload recovery freshness");
        Equal<string>(null, RuntimeConfigurationPolicy.Ipv6Action(false), "IPv4-compatible runtime continues normally");
        Equal<string>(null, RuntimeConfigurationPolicy.Ipv6Action(true),
            "IPv6 flag alone cannot disable monitoring or trigger configuration rewrites");
        Equal("🚀 节点选择", MonitorConfiguration.CreateDefault().GeneralGroup,
            "ordinary proxy group follows the stable selector");
        ServiceObservationBehavior();
        ExitNetworkEvidenceBehavior();
        RegionEligibilityCaching();
        CandidateFiltering();
        PipeHttpDecoding();
        BoundedPipeBehavior();
        MihomoPipeIntegration();
        CompatibilityScanning();
        FastFailoverWorkerOrchestration();
        ZLibraryWebBehavior();
        ZLibraryChoiceBehavior();
        DetailsTypographyBehavior();
        QualityScoringAndState();
        ThroughputAndTraffic();
        OpportunityOptimizationBehavior();
        OpportunityCandidatePlanningBehavior();
        AutomaticDecisionPolicyBehavior();
        AutomaticDecisionStateMachineBehavior();
        StabilityAndState();
        RuntimeGuards();
        SelectorFollowerBehavior();
        ProxyPathHealthBehavior();
        UserPreferenceBehavior();
        BrowserVerificationUiBehavior();
        MonitorCoordinatorBehavior();
        BrowserCoordinatorIntegrationBehavior();
        InstanceActivationBehavior();
        CommandLineBehavior();
        StatusReporting();
        ExperienceBehavior();
        ServiceIncidentBehavior();
        ServiceEvidenceBehavior();
        LoginChainEvidenceBehavior();
        AccountVerificationBehavior();
        BrowserConversationCoordinatorBehavior();
        BrowserNativeProtocolBehavior();
        BrowserBridgeBehavior();
        AssuranceBehavior();
        return failures == 0 ? 0 : 1;
    }

    private static void ServiceObservationBehavior()
    {
        DateTime observed = new DateTime(2026, 9, 21, 1, 2, 3, DateTimeKind.Utc);
        ServiceObservation success = ServiceObservation.FromProbe(ServiceKind.Google,
            ProbeResult.Success(123), observed, TestFingerprint('A'), "SG", 64500);
        Equal(ServiceOutcome.Success, success.Outcome, "definite pass maps to success");
        Equal(true, success.CountedForNodeHealth, "raw pass counts for node health");
        Equal(64500L, success.ExitAsn, "observation carries ASN");

        ServiceObservation failure = ServiceObservation.FromProbe(ServiceKind.ChatGPT,
            ProbeResult.RegionFailure("blocked", 456), observed, TestFingerprint('B'), "US", 64501);
        Equal(ServiceOutcome.Failure, failure.Outcome, "definite failure maps to failure");
        Equal(ProbeFailureKind.Region, failure.FailureKind, "failure kind is preserved");

        ServiceObservation partial = ServiceObservation.FromProbe(ServiceKind.Gemini,
            ProbeResult.Partial("reachable only", 78), observed, TestFingerprint('C'), "JP", null);
        Equal(ServiceOutcome.Unknown, partial.Outcome, "partial reachability maps to unknown");
        Equal(ProbeFailureKind.Partial, partial.FailureKind, "partial kind is preserved");

        ServiceObservation unverified = ServiceObservation.FromProbe(ServiceKind.Discord,
            ProbeResult.Unverified("not probed"), observed, "", "", null);
        Equal(ServiceOutcome.Unknown, unverified.Outcome, "unverified maps to unknown");
        Equal(false, unverified.WithNodeHealthCounting(false).CountedForNodeHealth,
            "history attribution can be disabled without changing outcome");
        Equal(ServiceOutcome.Unknown, unverified.WithNodeHealthCounting(false).Outcome,
            "history attribution leaves raw outcome unchanged");

        var legacy = new CandidateScanResult("legacy", CandidateHealth.Compatible, null, "ok");
        Equal(0, legacy.ServiceObservations.Count, "legacy scan defaults to no observations");
        Equal<long?>(null, legacy.ExitAsn, "legacy scan defaults to unknown ASN");
    }

    private static string TestFingerprint(char value)
    {
        return new string(value, 64);
    }

    private static void ExitNetworkEvidenceBehavior()
    {
        byte[] key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        string rawIp = "203.0.113.9";
        string expected = ExitIdentityKey.Fingerprint(rawIp, key);
        ExitAsnResolution matched = IpWhoExitAsnParser.Parse(
            "{\"success\":true,\"ip\":\"203.0.113.9\",\"country_code\":\"SG\",\"connection\":{\"asn\":64520}}",
            key, expected, "SG");
        Equal(true, matched.Matched, "IPWho identity matches Cloudflare evidence");
        Equal(64520L, matched.Asn, "IPWho ASN is parsed");
        Equal(false, matched.Detail.Contains(rawIp), "ASN diagnostics omit raw IP");

        Equal(false, IpWhoExitAsnParser.Parse(
            "{\"success\":true,\"ip\":\"203.0.113.10\",\"country_code\":\"SG\",\"connection\":{\"asn\":64520}}",
            key, expected, "SG").Matched, "fingerprint mismatch is rejected");
        Equal(false, IpWhoExitAsnParser.Parse(
            "{\"success\":true,\"ip\":\"203.0.113.9\",\"country_code\":\"US\",\"connection\":{\"asn\":64520}}",
            key, expected, "SG").Matched, "country mismatch is rejected");
        Equal(false, IpWhoExitAsnParser.Parse("not-json", key, expected, "SG").Matched,
            "malformed ASN response is unavailable evidence");

        DateTime now = new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc);
        var cache = new ExitNetworkEvidenceCache();
        cache.Remember(expected, "SG", 64520, now, TimeSpan.FromMinutes(60));
        long asn;
        Equal(true, cache.TryGet(expected, "SG", now.AddMinutes(59), out asn),
            "ASN cache is valid before sixty minutes");
        Equal(64520L, asn, "ASN cache returns the verified network");
        Equal(false, cache.TryGet(expected, "SG", now.AddMinutes(60), out asn),
            "ASN cache expires at sixty minutes");
        Equal(false, cache.TryGet(TestFingerprint('E'), "SG", now.AddMinutes(1), out asn),
            "different fingerprint misses cache");

        string root = Path.Combine(Path.GetTempPath(), "ccm-asn-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "exit-network.state");
        try
        {
            var store = new ExitNetworkEvidenceStore(path);
            store.Save(cache, now.AddMinutes(1));
            string json = File.ReadAllText(path);
            Equal(false, json.Contains(rawIp), "ASN cache never persists raw IP");
            ExitNetworkEvidenceCache loaded = store.Load(now.AddMinutes(1));
            Equal(1, loaded.Records.Count, "valid ASN cache survives restart");
            File.WriteAllText(path,
                "{\"Records\":[{\"ExitFingerprint\":\"bad\",\"CountryCode\":\"S\",\"Asn\":0}]}");
            Equal(0, store.Load(now).Records.Count, "malformed ASN records are rejected");

            var failedResolver = new FakeExitAsnResolver(new ExitAsnResolution {
                Matched = false, Asn = 0, Detail = "provider unavailable"
            });
            var enricher = new ExitNetworkEvidenceEnricher(failedResolver,
                new ExitNetworkEvidenceStore(Path.Combine(root, "provider-failure.state")));
            ExitIdentity original = new ExitIdentity(expected, "SG", "ok", null, now);
            ExitIdentity unchanged = enricher.Enrich(original, now, TimeSpan.FromSeconds(1));
            Equal(expected, unchanged.Fingerprint, "ASN provider failure preserves exit fingerprint");
            Equal("SG", unchanged.CountryCode, "ASN provider failure preserves exit country");
            Equal<long?>(null, unchanged.Asn, "ASN provider failure remains unknown evidence");
        }
        finally { DeleteDirectoryEventually(root); }
    }

    private static void RegionEligibilityCaching()
    {
        DateTime now = new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc);
        var cache = new RegionEligibilityCache();
        string exitA = 1L.ToString("X64");
        string exitB = 2L.ToString("X64");
        cache.Remember("scope-a", "node-a", exitA, "JP", now);
        string countryCode;
        bool supported;
        Equal(true, cache.TryGet("scope-a", "node-a", exitA, now, out countryCode, out supported),
            "fresh matching exit is cached");
        Equal("JP", countryCode, "cached country is returned");
        Equal(true, supported, "fresh Japan exit remains eligible");
        Equal(false, cache.TryGet("scope-a", "node-a", exitB, now, out countryCode, out supported),
            "different actual exit fingerprint invalidates region cache");
        Equal(false, cache.TryGet("scope-a", "node-a", "", now, out countryCode, out supported),
            "unknown actual exit cannot use a cached eligibility decision");
        Equal(false, cache.TryGet("scope-b", "node-a", exitA, now, out countryCode, out supported),
            "subscription scope change invalidates region cache");
        Equal(false, cache.TryGet("scope-a", "node-a", exitA, now.AddHours(24), out countryCode, out supported),
            "region cache expires exactly at 24 hours");

        string exitHk = 3L.ToString("X64");
        cache.Remember("scope-a", "node-hk", exitHk, "HK", now);
        Equal(true, cache.TryGet("scope-a", "node-hk", exitHk, now, out countryCode, out supported),
            "unsupported exit result is cached");
        Equal(false, supported, "cached Hong Kong exit remains ineligible");

        cache.Records.Add(new RegionEligibilityRecord {
            Scope = "scope-a", Node = "old-policy", ExitFingerprint = 4L.ToString("X64"),
            CountryCode = "JP", PolicyVersion = "old-policy", CheckedUtc = now
        });
        Equal(false, cache.TryGet("scope-a", "old-policy", 4L.ToString("X64"), now,
            out countryCode, out supported),
            "policy version change invalidates region cache");

        string exitNew = 5L.ToString("X64");
        cache.Remember("scope-a", "node-a", exitNew, "SG", now.AddMinutes(1));
        Equal(1, cache.Records.Count(x => x.Scope == "scope-a" && x.Node == "node-a"),
            "remember replaces an older matching scope and node");
        Equal(true, cache.TryGet("scope-a", "node-a", exitNew, now.AddMinutes(1),
            out countryCode, out supported),
            "replacement record is readable");
        Equal("SG", countryCode, "replacement record returns its new country");
        Throws<ArgumentException>(() => cache.Remember("scope-a", "raw-ip", "198.51.100.24", "JP", now),
            "region cache rejects a raw IP as an exit fingerprint");
        Throws<ArgumentException>(() => cache.Remember("scope-a", "bad-hash", new string('G', 64), "JP", now),
            "region cache rejects a non-hex exit fingerprint");
        Throws<ArgumentException>(() => cache.Remember("scope-a", "lower-hash", new string('a', 64), "JP", now),
            "region cache rejects a lowercase exit fingerprint");
        Equal(false, cache.TryRemember("scope-a", "unknown-exit", "test-fingerprint", "JP", now),
            "region cache safely ignores a non-canonical fingerprint");
        Equal(false, cache.Records.Any(x => x.Node == "unknown-exit"),
            "non-canonical fingerprint is never retained for persistence");

        var bounded = new RegionEligibilityCache();
        for (int i = 0; i < 300; i++)
            bounded.Remember("scope", "node-" + i, ((long)i + 10).ToString("X64"), "JP", now.AddMinutes(i));
        Equal(256, bounded.Records.Count, "region cache is bounded to 256 newest records");
        Equal("node-299", bounded.Records[0].Node, "region cache keeps newest record first");
        Equal(false, bounded.Records.Any(x => x.Node == "node-43"), "region cache prunes older records");
        Equal(true, bounded.Records.Any(x => x.Node == "node-44"), "region cache keeps the newest 256 records");

        string directory = Path.Combine(Path.GetTempPath(), "monitor-region-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "region-eligibility.json");
            string invalidOnlyPath = Path.Combine(directory, "invalid-only.json");
            var invalidOnly = new RegionEligibilityCache();
            Equal(false, invalidOnly.TryRemember("scope", "unknown", "test-fingerprint", "JP", now),
                "invalid-only cache safely declines a fake fingerprint");
            new RegionEligibilityStore(invalidOnlyPath).Save(invalidOnly);
            Equal(false, File.Exists(invalidOnlyPath),
                "invalid-only cache does not create a persistent file");
            string rawIp = "198.51.100.24";
            ExitIdentity identity = ExitIdentityParser.Parse("ip=" + rawIp + "\nloc=JP\n", new byte[] { 4, 5, 6 });
            var persisted = new RegionEligibilityCache();
            persisted.Remember("scope-save", "node-save", identity.Fingerprint, identity.CountryCode, now);
            for (int i = 0; i < 300; i++)
            {
                persisted.Records.Add(new RegionEligibilityRecord {
                    Scope = "scope-save", Node = "saved-node-" + i,
                    ExitFingerprint = ((long)i + 1000).ToString("X64"),
                    CountryCode = "JP", PolicyVersion = AiRegionPolicy.SnapshotDate,
                    CheckedUtc = now.AddMinutes(-i - 1)
                });
            }
            persisted.Records.Add(new RegionEligibilityRecord {
                Scope = "scope-save", Node = "malicious-raw-ip", ExitFingerprint = rawIp,
                CountryCode = "JP", PolicyVersion = AiRegionPolicy.SnapshotDate,
                CheckedUtc = now.AddMinutes(-2)
            });
            var store = new RegionEligibilityStore(path);
            store.Save(persisted);
            byte[] serialized = File.ReadAllBytes(path);
            Equal(false, serialized.Length >= 3 && serialized[0] == 0xEF && serialized[1] == 0xBB && serialized[2] == 0xBF,
                "region cache is saved as UTF-8 without BOM");
            string savedText = File.ReadAllText(path);
            Equal(false, savedText.Contains(rawIp), "region cache never persists a raw exit IP");
            Equal(256, new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<RegionEligibilityCache>(savedText).Records.Count,
                "region store bounds serialized records to 256");
            RegionEligibilityCache loaded = store.Load();
            Equal(true, loaded.TryGet("scope-save", "node-save", identity.Fingerprint, now,
                out countryCode, out supported),
                "region cache survives save and load");
            Equal("JP", countryCode, "round-trip preserves country code");
            Equal(identity.Fingerprint, loaded.Records[0].ExitFingerprint,
                "round-trip preserves only the hashed exit fingerprint");

            var hostile = new RegionEligibilityCache();
            hostile.Records.Add(new RegionEligibilityRecord {
                Scope = "scope-hostile", Node = "valid", ExitFingerprint = 6000L.ToString("X64"),
                CountryCode = "JP", PolicyVersion = AiRegionPolicy.SnapshotDate, CheckedUtc = DateTime.UtcNow
            });
            hostile.Records.Add(new RegionEligibilityRecord {
                Scope = "scope-hostile", Node = "raw", ExitFingerprint = rawIp,
                CountryCode = "JP", PolicyVersion = AiRegionPolicy.SnapshotDate, CheckedUtc = DateTime.UtcNow
            });
            hostile.Records.Add(new RegionEligibilityRecord {
                Scope = new string('s', 257), Node = "oversized", ExitFingerprint = 6001L.ToString("X64"),
                CountryCode = "JP", PolicyVersion = AiRegionPolicy.SnapshotDate, CheckedUtc = DateTime.UtcNow
            });
            hostile.Records.Add(new RegionEligibilityRecord {
                Scope = "scope-hostile", Node = new string('n', 513), ExitFingerprint = 6003L.ToString("X64"),
                CountryCode = "JP", PolicyVersion = AiRegionPolicy.SnapshotDate, CheckedUtc = DateTime.UtcNow
            });
            hostile.Records.Add(new RegionEligibilityRecord {
                Scope = "scope-hostile", Node = "oversized-policy", ExitFingerprint = 6004L.ToString("X64"),
                CountryCode = "JP", PolicyVersion = new string('p', 33), CheckedUtc = DateTime.UtcNow
            });
            hostile.Records.Add(new RegionEligibilityRecord {
                Scope = "scope-hostile", Node = "future", ExitFingerprint = 6002L.ToString("X64"),
                CountryCode = "JP", PolicyVersion = AiRegionPolicy.SnapshotDate,
                CheckedUtc = DateTime.UtcNow.AddMinutes(6)
            });
            File.WriteAllText(path, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(hostile));
            RegionEligibilityCache sanitized = store.Load();
            Equal(1, sanitized.Records.Count, "region store filters hostile and implausible records on load");
            Equal("valid", sanitized.Records[0].Node, "region store retains only a valid loaded record");

            File.WriteAllText(path, "{broken json");
            RegionEligibilityCache recovered = store.Load();
            Equal(0, recovered.Records.Count, "corrupt region cache recovers empty");
            Equal(1, Directory.GetFiles(directory, "region-eligibility.json.corrupt-*").Length,
                "corrupt region cache is archived");
            Equal(false, File.Exists(path), "corrupt source file is moved aside");
            File.WriteAllText(path, "{broken again");
            Equal(0, store.Load().Records.Count, "a second corrupt region cache also recovers empty");
            Equal(2, Directory.GetFiles(directory, "region-eligibility.json.corrupt-*").Length,
                "rapid corrupt recoveries use collision-resistant archive names");
            Equal(false, File.Exists(path), "second corrupt source file is also moved aside");

            string oversizedPath = Path.Combine(directory, "oversized.json");
            File.WriteAllText(oversizedPath, new string('x', 1024 * 1024 + 1));
            Equal(0, new RegionEligibilityStore(oversizedPath).Load().Records.Count,
                "oversized region cache is rejected before deserialization");
            Equal(false, File.Exists(oversizedPath), "oversized region cache source is archived");

            string concurrentPath = Path.Combine(directory, "region-concurrent.json");
            var concurrentErrors = new List<Exception>();
            var startConcurrent = new ManualResetEventSlim(false);
            Task[] concurrentTasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() => {
                var concurrentStore = new RegionEligibilityStore(worker % 2 == 0
                    ? concurrentPath : Path.Combine(directory, ".", "region-concurrent.json"));
                startConcurrent.Wait();
                for (int iteration = 0; iteration < 40; iteration++)
                {
                    try
                    {
                        var concurrentCache = new RegionEligibilityCache();
                        concurrentCache.Remember("scope-concurrent", "node-" + worker,
                            ((long)worker + 8000).ToString("X64"), "JP", DateTime.UtcNow);
                        concurrentStore.Save(concurrentCache);
                        concurrentStore.Load();
                    }
                    catch (Exception ex)
                    {
                        lock (concurrentErrors) concurrentErrors.Add(ex);
                    }
                }
            })).ToArray();
            startConcurrent.Set();
            Task.WaitAll(concurrentTasks);
            Equal(0, concurrentErrors.Count,
                "concurrent region store instances load and save without exceptions" +
                (concurrentErrors.Count == 0 ? "" : " (" + concurrentErrors[0].GetType().Name +
                    ": " + concurrentErrors[0].Message + ")"));
            RegionEligibilityCache concurrentLoaded = new RegionEligibilityStore(concurrentPath).Load();
            Equal(1, concurrentLoaded.Records.Count,
                "concurrent region store leaves a loadable final cache");
            Equal(0, Directory.GetFiles(directory, "region-concurrent.json.tmp*").Length,
                "concurrent region store cleans temporary files");
        }
        finally { DeleteDirectoryEventually(directory); }

        Throws<ArgumentException>(() => new RegionEligibilityStore(" "),
            "region store rejects a blank path");
        string relativePath = "region-cache-relative-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            bool relativeSaved = false;
            try
            {
                var relativeCache = new RegionEligibilityCache();
                relativeCache.Remember("scope-relative", "node-relative", 7000L.ToString("X64"), "JP", now);
                new RegionEligibilityStore(relativePath).Save(relativeCache);
                relativeSaved = File.Exists(Path.GetFullPath(relativePath));
            }
            catch { }
            Equal(true, relativeSaved, "region store supports a bare relative file name");
        }
        finally
        {
            string relativeFullPath = Path.GetFullPath(relativePath);
            if (File.Exists(relativeFullPath)) File.Delete(relativeFullPath);
        }
    }

    private static void ZLibraryWebBehavior()
    {
        ServiceKind service;
        Equal(true, Enum.TryParse("ZLibraryWeb", out service), "Z-Library service identity exists");
        Equal("https://zh.z-library.sk/", HttpServiceProbe.Endpoint(service).AbsoluteUri,
            "Z-Library uses the chosen HTTPS entrance");
        Equal(false, UserPreferences.Defaults().RequiredServices.Contains(service),
            "Z-Library is off by default");
        var probe = new FakeProbe();
        var scan = new CompatibilityScanner(new FakeMihomo(), probe, "probe")
            .ScanSelected(new CandidateNode("selected", 1), new[] { service });
        Equal(CandidateHealth.Compatible, scan.Health, "selected Z-Library service participates in scanning");
        Equal(1, probe.Calls.Count, "selected Z-Library sends one lightweight probe");
        Equal(service, probe.Calls[0], "selected Z-Library probes its own endpoint");
        Equal(ProbeFailureKind.None,
            HttpServiceProbe.EvaluateResponse(service, 200, "<html>library entrance</html>", null, 123).FailureKind,
            "Z-Library HTTP 200 is entrance evidence");
        Equal(ProbeFailureKind.Partial,
            HttpServiceProbe.EvaluateResponse(service, 200, "<html>cf-chl</html>", null, 123).FailureKind,
            "Z-Library challenge is reachability only");
        Equal(ProbeFailureKind.Partial,
            HttpServiceProbe.EvaluateResponse(service, 200, "<html>captcha</html>", null, 123).FailureKind,
            "Z-Library captcha is reachability only");
        Equal(ProbeFailureKind.Partial,
            HttpServiceProbe.EvaluateResponse(service, 200, "<html>sign in to continue</html>", null, 123).FailureKind,
            "Z-Library login challenge is reachability only");
        Equal(ProbeFailureKind.Partial,
            HttpServiceProbe.EvaluateResponse(service, 403, "", null, 123, true).FailureKind,
            "Z-Library HTTP 403 challenge header is reachability only");
        Equal(ProbeFailureKind.Partial,
            HttpServiceProbe.EvaluateResponse(service, 302, "", new Uri("https://unrelated.example/"), 123).FailureKind,
            "Z-Library cross-site redirect is not a success");
        Equal(ProbeFailureKind.Region,
            HttpServiceProbe.EvaluateResponse(service, 200, "not available in your region", null, 123).FailureKind,
            "Z-Library explicit region block fails");
        Equal(ProbeFailureKind.Service,
            HttpServiceProbe.EvaluateResponse(service, 403, "forbidden", null, 123).FailureKind,
            "Z-Library plain HTTP 403 fails");
        Equal(ProbeFailureKind.Service,
            HttpServiceProbe.EvaluateResponse(service, 500, "error", null, 123).FailureKind,
            "Z-Library server error fails");
        Equal("Z-Library 网页", MonitorPresentation.ServiceLabel(service),
            "Z-Library has its own service label");
        Equal("入口可达 · 123 ms",
            MonitorPresentation.ServiceText(new ServiceMeasurement(service, true, 123, "ok")),
            "Z-Library status does not claim login or downloads");
        Equal("网站功能未验证 · 123 ms",
            MonitorPresentation.ServiceText(new ServiceMeasurement(service, true, 123,
                "入口发生跳转，未验证网站功能", ProbeFailureKind.Partial)),
            "redirect is not presented as usable web access");
    }

    private static void ZLibraryChoiceBehavior()
    {
        var defaults = UserPreferences.Defaults();
        using (var form = new DetailsForm(defaults, delegate { }, delegate { }, delegate { },
            delegate { }, delegate { }, delegate { }, delegate { }))
        {
            CheckBox choice = FindCheckBox(form, "Z-Library 网页");
            Equal(true, choice != null, "Z-Library appears as an optional service");
            if (choice != null) Equal(false, choice.Checked, "Z-Library checkbox starts unchecked");
        }
        defaults.RequiredServices.Add(ServiceKind.ZLibraryWeb);
        using (var form = new DetailsForm(defaults, delegate { }, delegate { }, delegate { },
            delegate { }, delegate { }, delegate { }, delegate { }))
        {
            CheckBox choice = FindCheckBox(form, "Z-Library 网页");
            Equal(true, choice != null && choice.Checked, "saved Z-Library choice appears checked");
        }
        string directory = Path.Combine(Path.GetTempPath(), "monitor-zlibrary-choice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new UserPreferenceStore(Path.Combine(directory, "preferences.state"));
            store.Save(defaults);
            Equal(true, store.Load().RequiredServices.Contains(ServiceKind.ZLibraryWeb),
                "Z-Library choice persists across restart");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static CheckBox FindCheckBox(Control parent, string title)
    {
        foreach (Control child in parent.Controls)
        {
            var checkBox = child as CheckBox;
            if (checkBox != null && checkBox.Text == title) return checkBox;
            var nested = FindCheckBox(child, title);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void DetailsTypographyBehavior()
    {
        int[] narrow = ServiceTableLayout.ColumnWidths(280, 1F);
        Equal(true, narrow.All(width => width > 0) && narrow.Sum() <= 280,
            "narrow service table columns fit without a horizontal scrollbar");
        int[] normal = ServiceTableLayout.ColumnWidths(700, 1F);
        Equal(true, normal[0] >= 90 && normal[1] >= 180 && normal[2] >= 160,
            "normal service table keeps readable minimum column widths");
        Equal(true, normal.Sum() <= 700, "normal service table never exceeds its client width");
        int[] highDpi = ServiceTableLayout.ColumnWidths(1050, 1.5F);
        Equal(true, highDpi[0] >= 135 && highDpi[1] >= 270 && highDpi[2] >= 240,
            "high DPI service table scales its readable minimum widths");
        Equal(true, highDpi.Sum() <= 1050, "high DPI service table never exceeds its client width");

        using (var form = new DetailsForm(UserPreferences.Defaults(), delegate { },
            delegate { }, delegate { }, delegate { }, delegate { },
            delegate { }, delegate { }))
        {
            var fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var times = (Label)typeof(DetailsForm).GetField("nextCheckLabel", fields).GetValue(form);
            var services = (ListView)typeof(DetailsForm).GetField("serviceList", fields).GetValue(form);
            Equal("Segoe UI", times.Font.Name, "timestamps use compact Latin digits");
            Equal("Segoe UI", services.Font.Name, "service latency uses compact Latin digits");
            form.Show();
            form.Width = 640;
            Application.DoEvents();
            Equal(true, services.Columns.Cast<ColumnHeader>().Sum(column => column.Width) <= services.ClientSize.Width,
                "live service table relayout fits its actual client width");
        }
    }

    private static void ServiceEvidenceBehavior()
    {
        var strict = new CandidateScanResult("strict", CandidateHealth.Compatible, null, "ok");
        var reachable = new CandidateScanResult("reachable", CandidateHealth.BasicCompatible, null, "challenge");
        Equal(true, ServiceEvidencePolicy.CanHold(strict), "strict scan can hold");
        Equal(true, ServiceEvidencePolicy.CanHold(reachable), "reachable scan can hold without churn");
        Equal(true, ServiceEvidencePolicy.CanEmergencySwitch(strict), "strict scan can be emergency target");
        Equal(false, ServiceEvidencePolicy.CanEmergencySwitch(reachable), "reachable-only scan cannot be emergency target");

        var emergencyReachable = new CandidateScanResult("reachable", CandidateHealth.BasicCompatible, null, "login chain reachable", 300, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.ChatGPT, ProbeResult.Partial("entry reachable", 180) },
                { ServiceKind.Google, ProbeResult.Success(120) }
            });
        Equal(true, ServiceEvidencePolicy.CanFastFailoverTarget(emergencyReachable, ServiceKind.ChatGPT),
            "live target that passes failed service can rescue a broken current node");
        var emergencyBroken = new CandidateScanResult("broken", CandidateHealth.ServiceFailed, ServiceKind.ChatGPT, "blocked", 300, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.ChatGPT, ProbeResult.ServiceFailure("blocked", 180) },
                { ServiceKind.Google, ProbeResult.Success(120) }
            });
        Equal(false, ServiceEvidencePolicy.CanFastFailoverTarget(emergencyBroken, ServiceKind.ChatGPT),
            "target repeating the current failure cannot be used for fast failover");
        var emergencySlow = new CandidateScanResult("slow-target", CandidateHealth.Compatible, null, "ok", 2200, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.ChatGPT, ProbeResult.Success(500) },
                { ServiceKind.GitHub, ProbeResult.Success(2001) }
            });
        Equal(false, ServiceEvidencePolicy.CanFastFailoverTarget(emergencySlow, ServiceKind.ChatGPT),
            "fast failover refuses a target that still exceeds two seconds");

        var assurance = new ConnectionAssurance();
        assurance.Remember(reachable, "current", DateTime.UtcNow);
        Equal(0, assurance.Standbys.Count, "reachable-only scan cannot enter standby pool");
        assurance.Remember(strict, "current", DateTime.UtcNow);
        Equal(1, assurance.Standbys.Count, "strict scan enters standby pool");

        var state = new HealthState("scope");
        state.RememberPreferred("partial", CandidateHealth.BasicCompatible, DateTime.UtcNow);
        Equal("", state.PreferredNode, "reachable-only scan is not persisted as preferred");
    }

    private static void LoginChainEvidenceBehavior()
    {
        ProbeResult app = ProbeResult.Success(300);
        ProbeResult auth = ProbeResult.Success(400);
        Equal(ProbeFailureKind.None,
            HttpServiceProbe.CombineLoginChain(ServiceKind.ChatGPT, app, auth).FailureKind,
            "chatgpt app and auth success is login ready");
        Equal(ProbeFailureKind.Partial,
            HttpServiceProbe.CombineLoginChain(ServiceKind.ChatGPT,
                ProbeResult.Partial("challenge", 200), auth).FailureKind,
            "chatgpt challenge stays reachable only");
        Equal(ProbeFailureKind.None,
            HttpServiceProbe.CombineLoginChain(ServiceKind.Gemini,
                ProbeResult.LoginRedirect("accounts.google.com", 200), auth).FailureKind,
            "gemini exact login redirect plus auth success is login ready");
        Equal(ProbeFailureKind.Partial,
            HttpServiceProbe.CombineLoginChain(ServiceKind.Gemini,
                ProbeResult.Partial("unclassified page", 200), auth).FailureKind,
            "generic partial page cannot become login ready");
        Equal("https://auth.openai.com/.well-known/openid-configuration", HttpServiceProbe.AuthenticationEndpoint(ServiceKind.ChatGPT).AbsoluteUri,
            "chatgpt authentication endpoint");
        Equal("https://accounts.google.com/.well-known/openid-configuration", HttpServiceProbe.AuthenticationEndpoint(ServiceKind.Gemini).AbsoluteUri,
            "gemini authentication endpoint");
        Equal(ProbeFailureKind.None,
            HttpServiceProbe.EvaluateApplicationResponse(ServiceKind.ChatGPT, 200, "<html>ChatGPT</html>", null, 100).FailureKind,
            "chatgpt application page is raw login-chain success");
        Equal(ProbeFailureKind.Partial,
            HttpServiceProbe.EvaluateApplicationResponse(ServiceKind.ChatGPT, 403, "cf-chl", null, 100).FailureKind,
            "chatgpt challenge cannot become login ready");
        Equal(ProbeFailureKind.LoginRedirect,
            HttpServiceProbe.EvaluateApplicationResponse(ServiceKind.Gemini, 302, "",
                new Uri("https://accounts.google.com/ServiceLogin"), 100).FailureKind,
            "gemini exact authentication redirect is trusted intermediate evidence");
        Equal(ProbeFailureKind.Service,
            HttpServiceProbe.EvaluateApplicationResponse(ServiceKind.Gemini, 302, "",
                new Uri("https://accounts.google.com.evil.example/"), 100).FailureKind,
            "gemini lookalike authentication redirect is rejected");
        Equal(ProbeFailureKind.None,
            HttpServiceProbe.EvaluateAuthenticationResponse(ServiceKind.ChatGPT, 200,
                "{\"issuer\":\"https://auth.openai.com\"}", 100).FailureKind,
            "chatgpt OpenID issuer proves authentication infrastructure");
        Equal(ProbeFailureKind.Service,
            HttpServiceProbe.EvaluateAuthenticationResponse(ServiceKind.ChatGPT, 200,
                "<html>captive portal</html>", 100).FailureKind,
            "generic page cannot prove authentication infrastructure");
        Equal(ProbeFailureKind.Service,
            HttpServiceProbe.EvaluateAuthenticationResponse(ServiceKind.ChatGPT, 200,
                "{\"issuer\":\"https://auth.openai.com.evil.example\"}", 100).FailureKind,
            "lookalike OpenID issuer cannot prove authentication infrastructure");

        Equal(false, ChatGptSupportedRegions.Contains("HK"), "actual Hong Kong exit is ChatGPT region risk");
        Equal(true, ChatGptSupportedRegions.Contains("JP"), "Japan is in dated ChatGPT support snapshot");
        Equal(true, ChatGptSupportedRegions.Contains("SG"), "Singapore is in dated ChatGPT support snapshot");
        Equal(true, ChatGptSupportedRegions.Contains("TW"), "Taiwan is in dated ChatGPT support snapshot");
        Equal(false, ChatGptSupportedRegions.Contains(""), "unknown exit is not assumed supported");
        Equal(false, Object.ReferenceEquals(ChatGptSupportedRegions.AllCodes, ChatGptSupportedRegions.AllCodes),
            "ChatGPT supported-region enumeration returns defensive copies");
        Equal(true, AiRegionPolicy.SupportsBoth("JP"), "Japan is in the ChatGPT and Gemini intersection");
        Equal(true, AiRegionPolicy.SupportsBoth("SG"), "Singapore is in the ChatGPT and Gemini intersection");
        Equal(true, AiRegionPolicy.SupportsBoth("TW"), "Taiwan is in the ChatGPT and Gemini intersection");
        Equal(false, AiRegionPolicy.SupportsBoth("AF"), "Afghanistan is excluded because Gemini web does not support it");
        Equal(false, AiRegionPolicy.SupportsBoth("HK"), "Hong Kong is excluded because ChatGPT does not officially support it");
        Equal(false, AiRegionPolicy.SupportsBoth("CN"), "mainland China is excluded from the shared consumer-web intersection");
        Equal(false, AiRegionPolicy.SupportsBoth(""), "unknown exit is never assumed supported");
        Equal("2026-09-17", AiRegionPolicy.SnapshotDate, "region policy exposes its dated snapshot");

        ExitIdentity identity = ExitIdentityParser.Parse("ip=203.0.113.8\nloc=JP\ncolo=NRT\n", new byte[] { 1, 2, 3 });
        Equal("JP", identity.CountryCode, "trace country parsed");
        Equal(false, identity.Fingerprint.Contains("203.0.113.8"), "fingerprint hides raw exit IP");
        Equal(identity.Fingerprint,
            ExitIdentityParser.Parse("ip=203.0.113.8\nloc=JP\n", new byte[] { 1, 2, 3 }).Fingerprint,
            "same exit and key have stable fingerprint");

        var probe = new FakeProbe { DefaultResult = ProbeResult.Success(100) };
        CandidateScanResult supported = new CompatibilityScanner(new FakeMihomo(), probe, "probe",
            new FakeExitIdentityProbe(new ExitIdentity("exit-jp", "JP", "ok")))
            .ScanSelected(new CandidateNode("香港名称但日本出口", 1),
                new[] { ServiceKind.ChatGPT, ServiceKind.Gemini });
        Equal(CandidateHealth.Compatible, supported.Health, "actual supported exit wins over node label");
        Equal("JP", supported.ExitCountryCode, "scan carries actual exit country");
        Equal("exit-jp", supported.ExitFingerprint, "scan carries encrypted exit fingerprint");
        Equal("exit-jp", MonitorSnapshot.CreateRunning(supported.Name, supported, null, "ok",
            DateTime.UtcNow, DateTime.UtcNow).ExitFingerprint, "snapshot carries exit fingerprint for user verification");

        CandidateScanResult unsupported = new CompatibilityScanner(new FakeMihomo(), probe, "probe",
            new FakeExitIdentityProbe(new ExitIdentity("exit-hk", "HK", "ok")))
            .ScanSelected(new CandidateNode("日本名称但香港出口", 1),
                new[] { ServiceKind.ChatGPT, ServiceKind.Gemini });
        Equal(CandidateHealth.RegionBlocked, unsupported.Health,
            "actual Hong Kong exit blocks combined AI switch evidence");
        Equal(ServiceKind.ChatGPT, unsupported.FailedService.Value, "region gate identifies ChatGPT");

        CandidateScanResult unsupportedGemini = new CompatibilityScanner(new FakeMihomo(), probe, "probe",
            new FakeExitIdentityProbe(new ExitIdentity("exit-hk", "HK", "ok")))
            .ScanSelected(new CandidateNode("香港出口", 1), new[] { ServiceKind.Gemini });
        Equal(CandidateHealth.RegionBlocked, unsupportedGemini.Health,
            "shared region gate also blocks successful Gemini evidence");
        Equal<ServiceKind?>(ServiceKind.Gemini, unsupportedGemini.FailedService,
            "region gate identifies Gemini");

        var unknownProbe = new FakeProbe { DefaultResult = ProbeResult.Success(100) };
        unknownProbe.Results[ServiceKind.Gemini] = ProbeResult.Partial("入口可达", 80);
        CandidateScanResult unknownExit = new CompatibilityScanner(new FakeMihomo(), unknownProbe, "probe")
            .ScanSelected(new CandidateNode("未配置出口探针", 1),
                new[] { ServiceKind.ChatGPT, ServiceKind.Gemini });
        Equal(CandidateHealth.Unknown, unknownExit.Health,
            "missing exit identity probe cannot validate AI region eligibility");
        Equal(ProbeFailureKind.Unverified, unknownExit.ServiceResults[ServiceKind.ChatGPT].FailureKind,
            "unknown exit makes successful ChatGPT evidence unverified");
        Equal(ProbeFailureKind.Unverified, unknownExit.ServiceResults[ServiceKind.Gemini].FailureKind,
            "unknown exit makes partial Gemini evidence unverified");

        var failedProbe = new FakeProbe { DefaultResult = ProbeResult.Success(100) };
        failedProbe.Results[ServiceKind.Gemini] = ProbeResult.ServiceFailure("authentication rejected", 90);
        CandidateScanResult explicitFailure = new CompatibilityScanner(new FakeMihomo(), failedProbe, "probe",
            new FakeExitIdentityProbe(new ExitIdentity("exit-hk", "HK", "ok")))
            .ScanSelected(new CandidateNode("服务失败", 1), new[] { ServiceKind.Gemini });
        Equal(CandidateHealth.ServiceFailed, explicitFailure.Health,
            "region gate does not overwrite an explicit service failure");
        Equal(ProbeFailureKind.Service, explicitFailure.ServiceResults[ServiceKind.Gemini].FailureKind,
            "explicit AI service failure remains intact");
    }

    private static void AccountVerificationBehavior()
    {
        var data = new ExperienceData();
        DateTime now = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        AccountVerificationMemory.Mark(data, "scope", "node", "fingerprint", ServiceKind.ChatGPT,
            true, now, AccountVerificationMemory.CurrentRuleVersion);
        Equal(true, AccountVerificationMemory.IsValid(data, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(29), AccountVerificationMemory.CurrentRuleVersion),
            "account proof valid for same exit before thirty days");
        Equal(false, AccountVerificationMemory.IsBrowserConversationValid(data, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(1), BrowserConversationProof.CurrentProtocolVersion),
            "legacy manual proof cannot authorize browser proof");
        AccountVerificationMemory.MarkBrowserConversation(data, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now, BrowserConversationProof.CurrentProtocolVersion);
        Equal(true, AccountVerificationMemory.IsBrowserConversationValid(data, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(29), BrowserConversationProof.CurrentProtocolVersion),
            "browser proof valid for same exit before thirty days");
        Equal(false, AccountVerificationMemory.IsBrowserConversationValid(data, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(30), BrowserConversationProof.CurrentProtocolVersion),
            "browser proof expires at thirty days");
        Equal(false, AccountVerificationMemory.IsBrowserConversationValid(data, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(1), BrowserConversationProof.CurrentProtocolVersion + 1),
            "browser proof invalid after protocol upgrade");
        Equal(false, AccountVerificationMemory.IsValid(data, "scope", "node", "changed",
            ServiceKind.ChatGPT, now.AddDays(1), AccountVerificationMemory.CurrentRuleVersion),
            "account proof invalid after exit change");
        Equal(false, AccountVerificationMemory.IsValid(data, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(30), AccountVerificationMemory.CurrentRuleVersion),
            "account proof expires at thirty days");
        Equal(false, AccountVerificationMemory.IsValid(data, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(1), AccountVerificationMemory.CurrentRuleVersion + 1),
            "account proof invalid after evidence rule upgrade");

        var strict = new CandidateScanResult("node", CandidateHealth.Compatible, null, "ok", 200, 2,
            new Dictionary<ServiceKind, ProbeResult> {
                { ServiceKind.ChatGPT, ProbeResult.Success(100) },
                { ServiceKind.Gemini, ProbeResult.Success(100) }
            }, "fingerprint", "JP");
        Equal(true, ServiceEvidencePolicy.CanQualitySwitch(strict, data, "scope",
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, now),
            "quality switch uses complete network evidence without browser proof");
        Equal(true, ServiceEvidencePolicy.CanQualitySwitch(strict, new ExperienceData(), "scope",
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, now),
            "compatible network evidence no longer depends on browser proof");
        AccountVerificationMemory.MarkBrowserConversation(data, "scope", "node", "fingerprint", ServiceKind.Gemini,
            now, BrowserConversationProof.CurrentProtocolVersion);
        Equal(true, ServiceEvidencePolicy.CanQualitySwitch(strict, data, "scope",
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, now),
            "quality switch accepts current exit after both AI proofs");
        Equal(true, ServiceEvidencePolicy.CanQualitySwitch(strict, new ExperienceData(), "scope",
            new[] { ServiceKind.Google, ServiceKind.GitHub }, now),
            "non-AI configuration does not require account proof");
        Equal(true, ServiceEvidencePolicy.CanRestoreAfterReload(strict, new ExperienceData(), "scope",
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, now),
            "reload recovery accepts complete live network evidence");
        Equal(true, ServiceEvidencePolicy.CanRestoreAfterReload(strict, data, "scope",
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, now),
            "reload recovery accepts account-verified AI target");

        string path = Path.Combine(Path.GetTempPath(), "account-proof-" + Guid.NewGuid().ToString("N"), "experience.json");
        var store = new ExperienceStore(path);
        store.Save(data, now);
        ExperienceData loaded = store.Load();
        Equal(true, AccountVerificationMemory.IsBrowserConversationValid(loaded, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(1), BrowserConversationProof.CurrentProtocolVersion),
            "browser proof method survives restart");
        Equal(true, AccountVerificationMemory.IsValid(loaded, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(1), AccountVerificationMemory.CurrentRuleVersion),
            "account proof survives restart");
        Equal(false, File.ReadAllText(path).Contains("203.0.113.8"), "account proof store contains no raw exit IP");

        AccountVerificationMemory.Revoke(loaded, "scope", "node", ServiceKind.ChatGPT,
            now.AddDays(1), "explicit failure");
        Equal(false, AccountVerificationMemory.IsValid(loaded, "scope", "node", "fingerprint",
            ServiceKind.ChatGPT, now.AddDays(1), AccountVerificationMemory.CurrentRuleVersion),
            "explicit failure revokes account proof");

        string workerRoot = Path.Combine(Path.GetTempPath(), "account-worker-" + Guid.NewGuid().ToString("N"));
        var workerData = new ExperienceData { ActiveScope = "scope", Assurance = new ConnectionAssurance { Scope = "scope" } };
        workerData.Assurance.Begin("old-node", "node", 100, true, false, now);
        var workerStore = new ExperienceStore(Path.Combine(workerRoot, "state", "experience.json"));
        workerStore.Save(workerData, now);
        var workerConfig = new MonitorConfiguration { RootPath = workerRoot, SharedGroup = "shared", ProbeGroup = "probe" };
        var workerMihomo = new VerificationMihomo("node", new[] { "node", "old-node" });
        var workerProbe = new FakeProbe { DefaultResult = ProbeResult.Success(100) };
        var worker = new MonitorWorker(workerConfig, workerMihomo,
            workerProbe,
            new BoundedLogger(Path.Combine(workerRoot, "logs", "monitor.log"), 1024 * 1024),
            new FakeClock { UtcNow = now },
            new FakeExitIdentityProbe(new ExitIdentity("fresh-exit", "JP", "ok")));
        Equal(false, worker.RecordBrowserConversationProof("node", "stale-exit", ServiceKind.Gemini,
            now, BrowserConversationProof.CurrentProtocolVersion),
            "worker rejects browser proof after exit changed");
        Equal(false, worker.RecordBrowserConversationProof("node", "fresh-exit", ServiceKind.Gemini,
            now, BrowserConversationProof.CurrentProtocolVersion + 1),
            "worker rejects browser proof from unknown protocol");
        Equal(true, worker.RecordBrowserConversationProof("node", "fresh-exit", ServiceKind.Gemini,
            now, BrowserConversationProof.CurrentProtocolVersion),
            "worker accepts browser proof after fresh login-chain and exit check");
        Equal(true, AccountVerificationMemory.IsBrowserConversationValid(workerStore.Load(), "scope",
            "node", "fresh-exit", ServiceKind.Gemini, now,
            BrowserConversationProof.CurrentProtocolVersion),
            "worker persists freshly revalidated browser conversation proof");
        int beforeRollbackRecheck = workerProbe.Calls.Count;
        worker.ReportBrowserConversationFailure("node", "fresh-exit", ServiceKind.Gemini,
            BrowserVerificationOutcome.ConversationError, true, now.AddMinutes(2));
        Equal(true, workerProbe.Calls.Count > beforeRollbackRecheck,
            "browser failure performs fresh old-node service recheck");
        Equal("old-node", workerMihomo.GetSelected("shared"),
            "browser failure rechecks old node before safe rollback");

        string staleRoot = Path.Combine(Path.GetTempPath(), "browser-stale-" + Guid.NewGuid().ToString("N"));
        var staleData = new ExperienceData { ActiveScope = "scope", Assurance = new ConnectionAssurance { Scope = "scope" } };
        staleData.Assurance.Begin("old-node", "node", 100, true, false, now);
        new ExperienceStore(Path.Combine(staleRoot, "state", "experience.json")).Save(staleData, now);
        var staleMihomo = new VerificationMihomo("node", new[] { "node", "old-node" });
        var staleProbe = new FakeProbe { DefaultResult = ProbeResult.Success(100) };
        var staleWorker = new MonitorWorker(new MonitorConfiguration { RootPath = staleRoot, SharedGroup = "shared", ProbeGroup = "probe" },
            staleMihomo, staleProbe, new BoundedLogger(Path.Combine(staleRoot, "logs", "monitor.log"), 1024 * 1024),
            new FakeClock { UtcNow = now.AddMinutes(11) }, new FakeExitIdentityProbe(new ExitIdentity("fresh-exit", "JP", "ok")));
        staleWorker.ReportBrowserConversationFailure("node", "fresh-exit", ServiceKind.Gemini,
            BrowserVerificationOutcome.ConversationError, true, now.AddMinutes(2));
        Equal("node", staleMihomo.GetSelected("shared"),
            "delayed browser failure cannot use an expired rollback window");
        staleWorker.ReportServiceFailure("node", ServiceKind.Gemini, now.AddMinutes(2));
        Equal("node", staleMihomo.GetSelected("shared"),
            "delayed manual failure cannot use an expired rollback window");

        string driftRoot = Path.Combine(Path.GetTempPath(), "browser-exit-drift-" + Guid.NewGuid().ToString("N"));
        var driftData = new ExperienceData { ActiveScope = "scope", Assurance = new ConnectionAssurance { Scope = "scope" } };
        driftData.Assurance.Begin("old-node", "node", 100, true, false, now);
        AccountVerificationMemory.MarkBrowserConversation(driftData, "scope", "node", "changed-exit",
            ServiceKind.Gemini, now.AddMinutes(1), BrowserConversationProof.CurrentProtocolVersion);
        new ExperienceStore(Path.Combine(driftRoot, "state", "experience.json")).Save(driftData, now);
        var driftMihomo = new VerificationMihomo("node", new[] { "node", "old-node" });
        var driftWorker = new MonitorWorker(new MonitorConfiguration { RootPath = driftRoot, SharedGroup = "shared", ProbeGroup = "probe" },
            driftMihomo, new FakeProbe { DefaultResult = ProbeResult.Success(100) },
            new BoundedLogger(Path.Combine(driftRoot, "logs", "monitor.log"), 1024 * 1024),
            new FakeClock { UtcNow = now.AddMinutes(2) }, new FakeExitIdentityProbe(new ExitIdentity("changed-exit", "JP", "ok")));
        driftWorker.ReportBrowserConversationFailure("node", "fresh-exit", ServiceKind.Gemini,
            BrowserVerificationOutcome.ConversationError, true, now.AddMinutes(2));
        Equal("node", driftMihomo.GetSelected("shared"),
            "changed exit cannot use a stale browser failure for rollback");
        Equal(true, AccountVerificationMemory.IsBrowserConversationValid(
            new ExperienceStore(Path.Combine(driftRoot, "state", "experience.json")).Load(),
            "scope", "node", "changed-exit", ServiceKind.Gemini, now.AddMinutes(2),
            BrowserConversationProof.CurrentProtocolVersion),
            "old exit failure cannot revoke new exit conversation proof");
    }

    private static void BrowserConversationCoordinatorBehavior()
    {
        DateTime now = new DateTime(2026, 9, 11, 3, 0, 0, DateTimeKind.Utc);
        var clock = new FakeClock { UtcNow = now };
        var strict = new CandidateScanResult("node", CandidateHealth.Compatible, null, "ok", 200, 2,
            new Dictionary<ServiceKind, ProbeResult> {
                { ServiceKind.ChatGPT, ProbeResult.Success(100) },
                { ServiceKind.Gemini, ProbeResult.Success(100) }
            }, "exit", "JP");
        MonitorSnapshot snapshot = MonitorSnapshot.CreateRunning("node", strict, null, "ok", now, now.AddMinutes(1));
        var coordinator = new BrowserConversationCoordinator(clock, new FixedChallengeSource("CCM-A7F2"));

        Equal(BrowserVerificationStart.CompanionOffline,
            coordinator.StartCurrent(snapshot, new[] { ServiceKind.ChatGPT }, true),
            "browser verification requires online companion");
        coordinator.ObserveCompanion("Chrome", now);
        Equal(BrowserVerificationStart.ConsentRequired,
            coordinator.StartCurrent(snapshot, new[] { ServiceKind.ChatGPT }, false),
            "automatic browser verification requires consent");
        coordinator.SetConsent(true);
        Equal(BrowserVerificationStart.Started,
            coordinator.StartCurrent(snapshot, new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, false),
            "online consented companion starts verification");
        Equal(BrowserVerificationStart.Busy,
            coordinator.StartCurrent(snapshot, new[] { ServiceKind.ChatGPT }, true),
            "only one browser verification session runs");

        BrowserVerificationTask first = coordinator.Poll("Chrome", now);
        Equal(ServiceKind.ChatGPT, first.Service, "chatgpt browser verification runs first");
        Equal("CCM-A7F2", first.Challenge, "browser verification uses generated challenge");
        Equal(null, coordinator.Poll("Edge", now.AddSeconds(1)),
            "another browser cannot receive an active conversation task");
        Equal(null, coordinator.Poll("Chrome", now.AddSeconds(1)),
            "active conversation task is dispatched only once");
        var passed = new BrowserVerificationResult {
            TaskId = first.TaskId,
            Service = first.Service,
            Challenge = first.Challenge,
            Outcome = BrowserVerificationOutcome.Passed,
            MessageSent = true,
            ElapsedMilliseconds = 1234
        };
        Equal(BrowserVerificationAcceptance.Accepted,
            coordinator.Accept(passed, snapshot, now.AddSeconds(2)),
            "fresh matching browser result accepted");
        Equal(BrowserVerificationAcceptance.Replayed,
            coordinator.Accept(passed, snapshot, now.AddSeconds(3)),
            "browser result replay rejected");
        BrowserVerificationTask second = coordinator.Poll("Chrome", now.AddSeconds(3));
        Equal(ServiceKind.Gemini, second.Service, "gemini browser verification runs second");

        var changed = new CandidateScanResult("node", CandidateHealth.Compatible, null, "ok", 200, 2,
            strict.ServiceResults, "changed-exit", "JP");
        MonitorSnapshot changedSnapshot = MonitorSnapshot.CreateRunning("node", changed, null, "ok", now, now.AddMinutes(1));
        var drifted = new BrowserVerificationResult {
            TaskId = second.TaskId,
            Service = second.Service,
            Challenge = second.Challenge,
            Outcome = BrowserVerificationOutcome.Passed,
            MessageSent = true
        };
        Equal(BrowserVerificationAcceptance.Drifted,
            coordinator.Accept(drifted, changedSnapshot, now.AddSeconds(4)),
            "browser result rejected after exit drift");

        var expiry = new BrowserConversationCoordinator(clock, new FixedChallengeSource("CCM-B8E3"));
        expiry.ObserveCompanion("Edge", now);
        Equal(BrowserVerificationStart.Started,
            expiry.StartCurrent(snapshot, new[] { ServiceKind.ChatGPT }, true),
            "user initiated browser verification does not require persistent consent");
        BrowserVerificationTask expiring = expiry.Poll("Edge", now);
        var late = new BrowserVerificationResult {
            TaskId = expiring.TaskId,
            Service = expiring.Service,
            Challenge = expiring.Challenge,
            Outcome = BrowserVerificationOutcome.Passed,
            MessageSent = true
        };
        Equal(BrowserVerificationAcceptance.Expired,
            expiry.Accept(late, snapshot, now.AddMinutes(5)),
            "browser task expires at five minutes");

        var cooldown = new BrowserConversationCoordinator(clock, new FixedChallengeSource("CCM-C9D4"));
        cooldown.SetConsent(true);
        cooldown.ObserveCompanion("Chrome", now);
        Equal(BrowserVerificationStart.Started,
            cooldown.StartCurrent(snapshot, new[] { ServiceKind.ChatGPT }, false),
            "automatic browser verification starts before failure cooldown");
        BrowserVerificationTask failing = cooldown.Poll("Chrome", now);
        var failure = new BrowserVerificationResult {
            TaskId = failing.TaskId,
            Service = failing.Service,
            Challenge = failing.Challenge,
            Outcome = BrowserVerificationOutcome.GenerationTimeout,
            MessageSent = true
        };
        Equal(BrowserVerificationAcceptance.Accepted,
            cooldown.Accept(failure, snapshot, now.AddMinutes(1)),
            "sent generation timeout accepted as browser outcome");
        Equal(BrowserVerificationStart.NoEligibleServices,
            cooldown.StartCurrent(snapshot, new[] { ServiceKind.ChatGPT }, false),
            "automatic browser failure has six hour cooldown");
        Equal(BrowserVerificationStart.Started,
            cooldown.StartCurrent(snapshot, new[] { ServiceKind.ChatGPT }, true),
            "user retry bypasses automatic browser cooldown");
    }

    private sealed class FixedChallengeSource : IChallengeSource
    {
        private readonly string value;
        public FixedChallengeSource(string value) { this.value = value; }
        public string Create() { return value; }
    }

    private static void BrowserNativeProtocolBehavior()
    {
        Equal(BrowserConversationProof.CurrentProtocolVersion, BrowserNativeProtocol.ProtocolVersion,
            "browser proof and transport protocol versions stay aligned");
        var message = new BrowserBridgeMessage {
            Type = "result",
            ProtocolVersion = BrowserConversationProof.CurrentProtocolVersion,
            RequestId = "request-1",
            Browser = "Chrome",
            ExtensionVersion = "0.6.2",
            TaskId = "0123456789abcdef0123456789abcdef",
            Service = "ChatGPT",
            Challenge = "CCM-A7F2",
            Outcome = "Passed",
            MessageSent = true,
            ElapsedMilliseconds = 1234
        };
        var framed = new MemoryStream();
        BrowserNativeProtocol.Write(framed, message);
        byte[] bytes = framed.ToArray();
        Equal(true, bytes.Length > 4, "native message has length prefix and json body");
        Equal(bytes.Length - 4, BitConverter.ToInt32(bytes, 0), "native message uses little endian payload length");

        BrowserBridgeMessage roundTrip = BrowserNativeProtocol.Read(new FragmentedReadStream(bytes));
        Equal("result", roundTrip.Type, "native frame round trips across short reads");
        Equal("CCM-A7F2", roundTrip.Challenge, "native frame preserves challenge");
        Equal(true, BrowserMessageValidator.IsValidRequest(roundTrip), "valid native result request accepted");
        Equal(null, BrowserNativeProtocol.Read(new MemoryStream()), "clean native stream eof is allowed");

        Throws<EndOfStreamException>(() => BrowserNativeProtocol.Read(new MemoryStream(new byte[] { 1, 0 })),
            "partial native length prefix rejected");
        Throws<InvalidDataException>(() => BrowserNativeProtocol.Read(
            new MemoryStream(new byte[] { 1, 0, 1, 0 })), "oversized native frame rejected");
        Throws<InvalidDataException>(() => BrowserNativeProtocol.Read(
            new MemoryStream(new byte[] { 1, 0, 0, 0, (byte)'{' })), "invalid native json rejected");

        Equal(true, BrowserOriginPolicy.IsAllowed("chrome-extension://micoadiomajggfdfbnhjbpkbccjoldlg/"),
            "pinned extension origin accepted");
        Equal(false, BrowserOriginPolicy.IsAllowed("chrome-extension://micoadiomajggfdfbnhjbpkbccjoldlg.evil/"),
            "lookalike extension origin rejected");
        Equal(false, BrowserOriginPolicy.IsAllowed("https://chatgpt.com/"),
            "web page origin rejected by native host");

        message.RequestId = new string('x', 65);
        Equal(false, BrowserMessageValidator.IsValidRequest(message), "oversized request id rejected");
        message.RequestId = "request-1";
        message.Outcome = "EverythingFine";
        Equal(false, BrowserMessageValidator.IsValidRequest(message), "unknown result outcome rejected");
        message.Outcome = "Passed";
        message.Service = "Google";
        Equal(false, BrowserMessageValidator.IsValidRequest(message), "non-ai browser service rejected");
        message.Service = "ChatGPT";
        message.Challenge = new string('z', 81);
        Equal(false, BrowserMessageValidator.IsValidRequest(message), "oversized challenge rejected");
        message.Challenge = "CCM-测试";
        Equal(false, BrowserMessageValidator.IsValidRequest(message), "non-ascii challenge rejected");
        message.Challenge = "CCM-A7F2";
        message.Url = new string('u', 2049);
        Equal(false, BrowserMessageValidator.IsValidRequest(message), "oversized result url rejected");

        var poll = new BrowserBridgeMessage {
            Type = "poll",
            ProtocolVersion = BrowserConversationProof.CurrentProtocolVersion,
            RequestId = "poll-1",
            Browser = "Edge",
            ExtensionVersion = "0.6.2"
        };
        Equal(true, BrowserMessageValidator.IsValidRequest(poll), "valid native poll accepted");
        poll.ProtocolVersion++;
        Equal(false, BrowserMessageValidator.IsValidRequest(poll), "unknown native protocol version rejected");
        poll.ProtocolVersion--;
        poll.Type = "arbitrary";
        Equal(false, BrowserMessageValidator.IsValidRequest(poll), "unknown native message type rejected");
    }

    private sealed class FragmentedReadStream : MemoryStream
    {
        public FragmentedReadStream(byte[] bytes) : base(bytes) { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return base.Read(buffer, offset, Math.Min(count, 1));
        }
    }

    private static void BrowserBridgeBehavior()
    {
        string derived = BrowserPipeIdentity.ForCurrentUser();
        Equal(false, derived.IndexOf(Environment.UserName, StringComparison.OrdinalIgnoreCase) >= 0,
            "browser pipe name hides username");

        string pipe = "ccm-browser-test-" + Guid.NewGuid().ToString("N");
        int handled = 0;
        using (var server = new BrowserBridgeServer(pipe, request => {
            Interlocked.Increment(ref handled);
            return new BrowserBridgeMessage {
                Type = request.Type == "poll" ? "idle" : request.Type == "result" ? "ack" : "ready",
                ProtocolVersion = BrowserConversationProof.CurrentProtocolVersion,
                RequestId = request.RequestId
            };
        }))
        {
            server.Start();
            BrowserBridgeMessage ready = BrowserHostBridge.RoundTrip(pipe,
                ValidBrowserRequest("hello", "bridge-hello"), TimeSpan.FromSeconds(2));
            Equal("ready", ready.Type, "host bridges hello request response");
            BrowserBridgeMessage idle = BrowserHostBridge.RoundTrip(pipe,
                ValidBrowserRequest("poll", "bridge-poll"), TimeSpan.FromSeconds(2));
            Equal("idle", idle.Type, "host bridges long poll response");

            var result = ValidBrowserRequest("result", "bridge-result");
            result.TaskId = "0123456789abcdef0123456789abcdef";
            result.Service = "Gemini";
            result.Challenge = "CCM-B8E3";
            result.Outcome = "Passed";
            result.MessageSent = true;
            result.ElapsedMilliseconds = 1800;
            Equal("ack", BrowserHostBridge.RoundTrip(pipe, result, TimeSpan.FromSeconds(2)).Type,
                "host bridges browser result");

            BrowserBridgeMessage malformed = ValidBrowserRequest("unknown", "bridge-invalid");
            Equal("error", BrowserHostBridge.RoundTrip(pipe, malformed, TimeSpan.FromSeconds(2)).Type,
                "bridge rejects malformed request");
            Equal(3, handled, "malformed bridge request never reaches monitor handler");

            using (var abandoned = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
                abandoned.Connect(2000);
            Equal("ready", BrowserHostBridge.RoundTrip(pipe,
                ValidBrowserRequest("hello", "bridge-after-disconnect"), TimeSpan.FromSeconds(2)).Type,
                "bridge survives clean client disconnect");
            Equal(null, server.LastError, "clean bridge disconnect is not a server error");
        }

        BrowserBridgeMessage unavailable = BrowserHostBridge.RoundTrip(
            "ccm-browser-missing-" + Guid.NewGuid().ToString("N"),
            ValidBrowserRequest("hello", "bridge-missing"), TimeSpan.FromMilliseconds(100));
        Equal("unavailable", unavailable.Type, "native host reports monitor unavailable");
        Equal("bridge-missing", unavailable.RequestId, "unavailable response retains request correlation");
    }

    private static BrowserBridgeMessage ValidBrowserRequest(string type, string requestId)
    {
        return new BrowserBridgeMessage {
            Type = type,
            ProtocolVersion = BrowserConversationProof.CurrentProtocolVersion,
            RequestId = requestId,
            Browser = "Chrome",
            ExtensionVersion = "0.6.2"
        };
    }

    private static void AssuranceBehavior()
    {
        DateTime now = DateTime.UtcNow;
        var policy = new ConnectionAssurance();
        policy.SetScope("scope");
        var healthy = new CandidateScanResult("b", CandidateHealth.Compatible, null, "ok", 100, 1);
        var bad = new CandidateScanResult("b", CandidateHealth.Transient, null, "timeout");
        policy.Remember(healthy, "a", now);
        Equal(1, policy.Available(new[] { "a", "b" }, "a", now).Length, "fresh standby available");
        Equal(0, policy.Available(new[] { "a", "b" }, "a", now.AddMinutes(11)).Length, "expired standby excluded");
        Equal(0, policy.Available(new[] { "a" }, "a", now).Length, "removed standby excluded");
        policy.Remember(bad, "a", now);
        Equal(0, policy.Available(new[] { "b" }, "a", now).Length, "failed recheck evicts standby");
        policy.Remember(healthy, "a", now);
        policy.SetScope("new services");
        Equal(0, policy.Standbys.Count, "changed requirements invalidate standby");
        policy.Begin("a", "b", 100, true);
        Equal(true, policy.NeedsRollback(bad), "quality switch first failure requests rollback verification");
        policy.Begin("a", "b", 100, false);
        Equal(false, policy.NeedsRollback(bad), "failure switch first failure stays");
        Equal(true, policy.NeedsRollback(bad), "failure switch second failure requests rollback verification");
        policy.Begin("a", "b", 100, false, true);
        Equal(true, policy.NeedsRollback(bad), "unproven emergency target first explicit failure requests rollback verification");
        policy.Begin("a", "b", 100, false, true, now);
        Equal(true, policy.CanUserFeedbackRollback("b", now.AddMinutes(1)), "recent active switch permits feedback rollback");
        Equal(false, policy.CanUserFeedbackRollback("manual", now.AddMinutes(1)), "manual node cannot use stale rollback origin");
        Equal(false, policy.CanUserFeedbackRollback("b", now.AddMinutes(11)), "expired switch transaction cannot use rollback origin");
        Equal(true, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.ConversationError, true, now.AddMinutes(2)),
            "sent conversation error rechecks old node");
        Equal(true, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.GenerationTimeout, true, now.AddMinutes(2)),
            "sent generation timeout rechecks old node");
        Equal(false, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.Passed, true, now.AddMinutes(2)),
            "successful browser verification never rolls back");
        Equal(false, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.SignInRequired, false, now.AddMinutes(2)),
            "sign in state is not node failure");
        Equal(false, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.AutomationUnsupported, false, now.AddMinutes(2)),
            "unsupported automation is not node failure");
        Equal(false, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.ChallengeRequired, false, now.AddMinutes(2)),
            "browser challenge is not node failure");
        Equal(false, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.Cancelled, false, now.AddMinutes(2)),
            "cancelled browser verification is not node failure");
        Equal(false, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.ConversationError, false, now.AddMinutes(2)),
            "unsent conversation error cannot trigger rollback");
        Equal(false, policy.ShouldRecheckPreviousAfterBrowserResult("manual",
            BrowserVerificationOutcome.ConversationError, true, now.AddMinutes(2)),
            "manual selection cannot use stale browser rollback origin");
        Equal(false, policy.ShouldRecheckPreviousAfterBrowserResult("b",
            BrowserVerificationOutcome.ConversationError, true, now.AddMinutes(11)),
            "stale switch cannot use browser rollback origin");
        policy.Begin("a", "b", 100, true);
        var slow = new CandidateScanResult("b", CandidateHealth.Compatible, null, "ok", 200, 1);
        Equal(false, policy.NeedsRollback(slow), "one slow response does not roll back");
        Equal(true, policy.NeedsRollback(slow), "persistent regression requests rollback");
        policy.Begin("a", "b", 100, true);
        policy.NeedsRollback(bad);
        Equal(false, policy.NeedsRollback(healthy), "healthy check clears failure streak");
        Equal(TimeSpan.FromSeconds(30), policy.Interval(healthy), "pending switch uses short observation interval");
        policy.Target = null;
        policy.Interval(healthy); policy.Interval(healthy);
        Equal(TimeSpan.FromMinutes(1), policy.Interval(healthy), "automatic guard rechecks a stable connection within one minute");
        Equal(TimeSpan.FromSeconds(30), policy.Interval(bad), "failure restores fast checks");
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        policy.Begin("a", "b", 100, true);
        var restored = serializer.Deserialize<ConnectionAssurance>(serializer.Serialize(policy));
        Equal("b", restored.Target, "switch observation survives restart");
        Equal("a", restored.Previous, "rollback origin survives restart");
        policy.Target = null;
        policy.Previous = null;
        policy.PendingOptimization = new PendingOptimization { Scope = "scope", Current = "b", Target = "c",
            BaselineResponse = 1000, TargetResponse = 500, CreatedUtc = now };
        policy.LastOpportunityScanUtc = now;
        restored = serializer.Deserialize<ConnectionAssurance>(serializer.Serialize(policy));
        Equal("c", restored.PendingOptimization.Target, "pending optimization survives restart");
        Equal(null, restored.Target, "pending optimization is separate from switch observation");
        restored.Begin("b", "c", 1000, true, false, now);
        Equal(null, restored.PendingOptimization, "starting a real switch clears pending optimization");

        policy.Decision = new AutomaticDecisionTransaction {
            State = AutomaticDecisionState.ConfirmingOptimization,
            Scope = "scope", Current = "b", Target = "c",
            BaselineResponse = 1000, TargetResponse = 500,
            StartedUtc = now, ExpiresUtc = now.AddMinutes(2), Revision = 7
        };
        policy.AutomaticSwitches = new List<AutomaticSwitchRecord> {
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-9), From = "a", To = "b", Reason = "recovery" },
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-1), From = "b", To = "c", Reason = "optimization" }
        };
        restored = serializer.Deserialize<ConnectionAssurance>(serializer.Serialize(policy));
        Equal(AutomaticDecisionState.ConfirmingOptimization, restored.Decision.State,
            "automatic decision state survives restart");
        Equal("c", restored.Decision.Target,
            "automatic decision target survives restart");
        Equal(7L, restored.Decision.Revision,
            "automatic decision revision survives restart");
        Equal(2, restored.AutomaticSwitches.Count,
            "automatic switch budget history survives restart");

        var legacyPending = new ConnectionAssurance {
            PendingOptimization = new PendingOptimization { Scope = "scope", Current = "b", Target = "c",
                BaselineResponse = 1000, TargetResponse = 500, CreatedUtc = now }
        };
        Equal(AutomaticDecisionState.ConfirmingOptimization,
            legacyPending.EnsureDecisionState("b", now.AddSeconds(30)).State,
            "legacy pending optimization migrates to confirmation state");
        var legacyObservation = new ConnectionAssurance { Target = "b", Previous = "a", StartedUtc = now };
        Equal(AutomaticDecisionState.Observing,
            legacyObservation.EnsureDecisionState("b", now.AddSeconds(30)).State,
            "legacy switch target migrates to observation state");
        var legacyHold = new ConnectionAssurance { HoldUntilUtc = now.AddMinutes(5) };
        Equal(AutomaticDecisionState.Cooldown,
            legacyHold.EnsureDecisionState("b", now).State,
            "legacy future hold migrates to cooldown state");
        Equal(AutomaticDecisionState.Healthy,
            new ConnectionAssurance().EnsureDecisionState("b", now).State,
            "empty legacy assurance starts healthy");

        var activeRecovery = new ConnectionAssurance();
        activeRecovery.EnsureDecisionState("a", now);
        activeRecovery.Apply(AutomaticDecisionEvent.HardFailure,
            new AutomaticDecisionContext { NowUtc = now, Current = "a" });
        activeRecovery.Apply(AutomaticDecisionEvent.FailureConfirmed,
            new AutomaticDecisionContext { NowUtc = now, Current = "a" });
        DecisionTransition recoveryReady = activeRecovery.Apply(AutomaticDecisionEvent.RecoveryTargetReady,
            new AutomaticDecisionContext { NowUtc = now, Current = "a", Previous = "a", Target = "b" });
        Equal(true, recoveryReady.Accepted,
            "active recovery events are not normalized as interrupted persistence");
        Equal(AutomaticDecisionState.Switching, activeRecovery.Decision.State,
            "active recovery reaches switching through assurance events");

        var interrupted = new ConnectionAssurance {
            Decision = new AutomaticDecisionTransaction {
                State = AutomaticDecisionState.SearchingOptimization,
                Current = "b", StartedUtc = now.AddSeconds(-10)
            }
        };
        Equal(AutomaticDecisionState.Degraded,
            interrupted.EnsureDecisionState("b", now).State,
            "interrupted search normalizes to degraded after restart");
        var expiredCooldown = new ConnectionAssurance {
            Decision = new AutomaticDecisionTransaction {
                State = AutomaticDecisionState.Cooldown,
                Current = "b", ExpiresUtc = now.AddSeconds(-1)
            }
        };
        Equal(AutomaticDecisionState.Healthy,
            expiredCooldown.EnsureDecisionState("b", now).State,
            "expired cooldown normalizes to healthy after restart");
        policy.SetScope("another scope");
        Equal(AutomaticDecisionState.Healthy, policy.Decision.State,
            "scope change resets automatic decision state");
        Equal<string>(null, policy.Decision.Target,
            "scope change clears automatic decision target");
    }

    private static void ServiceIncidentBehavior()
    {
        DateTime now = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        ServiceKind service = ServiceKind.Discord;
        var current = IncidentScan("current", service, ProbeFailureKind.Service);
        var sameA = IncidentScan("a", service, ProbeFailureKind.Service);
        var sameB = IncidentScan("b", service, ProbeFailureKind.Service);
        Equal(true, ServiceIncidentPolicy.HasConsensus(current, new[] { sameA, sameB }, service),
            "two distinct backups confirm service incident");
        Equal(false, ServiceIncidentPolicy.HasConsensus(current, new[] { sameA }, service),
            "one backup cannot confirm service incident");
        var passed = new CandidateScanResult("b", CandidateHealth.Compatible, null, "ok", 20, 1,
            new Dictionary<ServiceKind, ProbeResult> { { service, ProbeResult.Success(20) } });
        Equal(false, ServiceIncidentPolicy.HasConsensus(current, new[] { sameA, passed }, service),
            "passing backup rejects service incident");
        var transient = IncidentScan("b", service, ProbeFailureKind.Transient);
        Equal(false, ServiceIncidentPolicy.HasConsensus(current, new[] { sameA, transient }, service),
            "different failure kinds reject service incident");
        Equal(false, ServiceIncidentPolicy.HasConsensus(current, new[] { sameA, IncidentScan("a", service, ProbeFailureKind.Service) }, service),
            "duplicate backup node cannot confirm service incident");
        Equal(false, ServiceIncidentPolicy.HasConsensus(current, new[] { IncidentScan("current", service, ProbeFailureKind.Service), sameA }, service),
            "current node cannot be counted as a backup confirmation");

        ServiceIncidentConsensus sameExit = ServiceIncidentPolicy.EvaluateConsensus(
            IncidentObservationScan("current", TestFingerprint('A'), "SG", 64530, service),
            new[] {
                IncidentObservationScan("node-2", TestFingerprint('A'), "SG", 64530, service),
                IncidentObservationScan("node-3", TestFingerprint('A'), "SG", 64530, service)
            }, service);
        Equal(false, sameExit.Passed, "three names on one exit cannot open incident");
        Equal(1, sameExit.DistinctFingerprintCount, "duplicate exits are counted once");

        ServiceIncidentConsensus oneAsn = ServiceIncidentPolicy.EvaluateConsensus(
            IncidentObservationScan("current", TestFingerprint('A'), "SG", 64530, service),
            new[] {
                IncidentObservationScan("node-2", TestFingerprint('B'), "SG", 64530, service),
                IncidentObservationScan("node-3", TestFingerprint('C'), "JP", 64530, service)
            }, service);
        Equal(false, oneAsn.Passed, "one known ASN cannot establish diversity");

        ServiceIncidentConsensus twoAsns = ServiceIncidentPolicy.EvaluateConsensus(
            IncidentObservationScan("current", TestFingerprint('A'), "SG", 64530, service),
            new[] {
                IncidentObservationScan("node-2", TestFingerprint('B'), "SG", 64531, service),
                IncidentObservationScan("node-3", TestFingerprint('C'), "JP", 64531, service)
            }, service);
        Equal(true, twoAsns.Passed, "two known ASNs establish independent consensus");
        Equal(3, twoAsns.DistinctFingerprintCount, "consensus counts three independent exits");
        Equal(2, twoAsns.DistinctAsnCount, "consensus counts distinct ASNs");

        ServiceIncidentConsensus countryFallback = ServiceIncidentPolicy.EvaluateConsensus(
            IncidentObservationScan("current", TestFingerprint('A'), "SG", 64530, service),
            new[] {
                IncidentObservationScan("node-2", TestFingerprint('B'), "JP", null, service),
                IncidentObservationScan("node-3", TestFingerprint('C'), "SG", 64530, service)
            }, service);
        Equal(true, countryFallback.Passed, "country fallback applies when ASN is incomplete");

        ServiceIncidentConsensus noDiversity = ServiceIncidentPolicy.EvaluateConsensus(
            IncidentObservationScan("current", TestFingerprint('A'), "SG", 64530, service),
            new[] {
                IncidentObservationScan("node-2", TestFingerprint('B'), "SG", null, service),
                IncidentObservationScan("node-3", TestFingerprint('C'), "SG", null, service)
            }, service);
        Equal(false, noDiversity.Passed, "incomplete ASN with one country is rejected");
        Equal("insufficient-country-diversity", noDiversity.RejectionReason,
            "consensus rejection is explainable without node names");

        var incidents = new List<ServiceIncidentRecord>();
        ServiceIncidentPolicy.Open(incidents, service, ProbeFailureKind.Service, now, TimeSpan.FromMinutes(10));
        Equal(true, ServiceIncidentPolicy.IsActive(incidents, service, now.AddMinutes(9)), "service circuit active before expiry");
        Equal(false, ServiceIncidentPolicy.IsActive(incidents, service, now.AddMinutes(10)), "service circuit expires at boundary");
        var remaining = ServiceIncidentPolicy.ServicesToProbe(new[] { ServiceKind.ChatGPT, service }, incidents, now.AddMinutes(1));
        Equal("ChatGPT", String.Join(",", remaining), "active incident is omitted while other services continue");
        var healthyOthers = new CandidateScanResult("current", CandidateHealth.Compatible, null, "ok", 40, 1,
            new Dictionary<ServiceKind, ProbeResult> { { ServiceKind.ChatGPT, ProbeResult.Success(40) } }, "exit-fp", "JP");
        CandidateScanResult suppressed = ServiceIncidentPolicy.AttachSuppressed(healthyOthers, new[] { service });
        Equal(CandidateHealth.Unknown, suppressed.Health, "suppressed endpoint does not mark node healthy or failed");
        Equal(ProbeFailureKind.Unverified, suppressed.ServiceResults[service].FailureKind, "suppressed endpoint is explicit in evidence");
        Equal("exit-fp", suppressed.ExitFingerprint, "service suppression preserves exit fingerprint");
        Equal("JP", suppressed.ExitCountryCode, "service suppression preserves exit country");
        CandidateScanResult suppressedOwnFailure = ServiceIncidentPolicy.AttachSuppressed(current, new[] { service });
        Equal(CandidateHealth.ServiceFailed, suppressedOwnFailure.Health,
            "an active circuit does not erase a real current-cycle failure");
        Equal(ProbeFailureKind.Service, suppressedOwnFailure.ServiceResults[service].FailureKind,
            "an active circuit preserves real service evidence already probed");
        Equal(ServiceOutcome.Failure, suppressedOwnFailure.ServiceObservations[service].Outcome,
            "suppression preserves raw failure outcome");
        Equal(false, suppressedOwnFailure.ServiceObservations[service].CountedForNodeHealth,
            "suppressed failure is excluded from node history");
        CandidateScanResult suppressedHealth = ServiceIncidentPolicy.ForNodeHealth(suppressedOwnFailure);
        Equal(CandidateHealth.Unknown, suppressedHealth.Health,
            "only suppressed evidence produces unknown node-health view");
        Equal(0, suppressedHealth.ServiceResults.Count,
            "node-health view excludes suppressed service measurements");

        ProbeResult chatFailure = ProbeResult.ServiceFailure("shared outage", 900);
        ProbeResult githubSuccess = ProbeResult.Success(100);
        var mixedResults = new Dictionary<ServiceKind, ProbeResult> {
            { ServiceKind.ChatGPT, chatFailure }, { ServiceKind.GitHub, githubSuccess }
        };
        var mixedObservations = new Dictionary<ServiceKind, ServiceObservation> {
            { ServiceKind.ChatGPT, ServiceObservation.FromProbe(ServiceKind.ChatGPT, chatFailure,
                now, TestFingerprint('A'), "SG", 64530) },
            { ServiceKind.GitHub, ServiceObservation.FromProbe(ServiceKind.GitHub, githubSuccess,
                now, TestFingerprint('A'), "SG", 64530) }
        };
        var mixed = new CandidateScanResult("mixed", CandidateHealth.ServiceFailed, ServiceKind.ChatGPT,
            "shared outage", 1000, 2, mixedResults, TestFingerprint('A'), "SG", mixedObservations, 64530);
        CandidateScanResult mixedSuppressed = ServiceIncidentPolicy.AttachSuppressed(mixed,
            new[] { ServiceKind.ChatGPT });
        CandidateScanResult mixedHealth = ServiceIncidentPolicy.ForNodeHealth(mixedSuppressed);
        Equal(CandidateHealth.Compatible, mixedHealth.Health,
            "unsuppressed success remains usable for node history");
        Equal(1, mixedHealth.ServiceResults.Count,
            "history measurements contain only attributable services");
        Equal(true, mixedHealth.ServiceResults.ContainsKey(ServiceKind.GitHub),
            "history retains the attributable service measurement");
        var unrelatedFailure = IncidentScan("current", ServiceKind.ChatGPT, ProbeFailureKind.Transient);
        CandidateScanResult preservedFailure = ServiceIncidentPolicy.AttachSuppressed(unrelatedFailure, new[] { service });
        Equal(CandidateHealth.Transient, preservedFailure.Health, "suppression preserves failures from other services");
        Equal(ServiceKind.ChatGPT, preservedFailure.FailedService.Value, "suppression preserves unrelated failed service");

        var data = new ExperienceData();
        data.ServiceIncidents.AddRange(incidents);
        string path = Path.Combine(Path.GetTempPath(), "monitor-incidents-" + Guid.NewGuid().ToString("N"), "experience.json");
        var store = new ExperienceStore(path);
        store.Save(data, now);
        Equal(true, ServiceIncidentPolicy.IsActive(store.Load().ServiceIncidents, service, now.AddMinutes(1)),
            "service circuit survives restart");
    }

    private static CandidateScanResult IncidentScan(string node, ServiceKind service, ProbeFailureKind kind)
    {
        long asn = node == "current" ? 64530 : 64531;
        string country = node == "current" ? "SG" : "JP";
        return IncidentObservationScan(node, TestNodeFingerprint(node), country, asn, service, kind);
    }

    private static CandidateScanResult IncidentObservationScan(string node, string fingerprint,
        string country, long? asn, ServiceKind service, ProbeFailureKind kind = ProbeFailureKind.Service)
    {
        ProbeResult result = kind == ProbeFailureKind.Region ? ProbeResult.RegionFailure("same", 20) :
            kind == ProbeFailureKind.Transient ? ProbeResult.TransientFailure("same", 20) : ProbeResult.ServiceFailure("same", 20);
        CandidateHealth health = kind == ProbeFailureKind.Region ? CandidateHealth.RegionBlocked :
            kind == ProbeFailureKind.Transient ? CandidateHealth.Transient : CandidateHealth.ServiceFailed;
        DateTime observed = new DateTime(2026, 9, 21, 4, 0, 0, DateTimeKind.Utc);
        var observations = new Dictionary<ServiceKind, ServiceObservation> {
            { service, ServiceObservation.FromProbe(service, result, observed, fingerprint, country, asn) }
        };
        return new CandidateScanResult(node, health, service, "same", 20, 1,
            new Dictionary<ServiceKind, ProbeResult> { { service, result } }, fingerprint, country,
            observations, asn);
    }

    private static string TestNodeFingerprint(string node)
    {
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(node ?? ""))).Replace("-", "");
    }

    private static void ExperienceBehavior()
    {
        DateTime now = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);
        var continuity = new ExperienceData();
        string originalScope = continuity.ResolveScope("first", new[] { "a", "b", "c", "d" }, "chatgpt,gemini");
        Equal("first|chatgpt,gemini", originalScope, "initial subscription creates scope");
        Equal(originalScope, continuity.ResolveScope("reordered", new[] { "d", "c", "b", "a" }, "chatgpt,gemini"),
            "candidate reorder preserves experience scope");
        Equal(originalScope, continuity.ResolveScope("minor", new[] { "a", "b", "c", "e", "f" }, "chatgpt,gemini"),
            "minor subscription update preserves experience scope");
        Equal("different|chatgpt,gemini", continuity.ResolveScope("different", new[] { "w", "x", "y", "z" }, "chatgpt,gemini"),
            "different airport creates isolated experience scope");
        Equal("services|github", continuity.ResolveScope("services", new[] { "w", "x", "y", "z" }, "github"),
            "service change creates isolated experience scope");
        var singleContinuity = new ExperienceData();
        string singleScope = singleContinuity.ResolveScope("single-1", new[] { "only" }, "google");
        Equal(singleScope, singleContinuity.ResolveScope("single-2", new[] { "only" }, "google"),
            "unchanged single-node subscription preserves scope");
        Equal("single-3|google", singleContinuity.ResolveScope("single-3", new[] { "replacement" }, "google"),
            "replaced single-node subscription creates scope");
        var legacyContinuity = new ExperienceData { ActiveScope = "legacy|chatgpt,gemini" };
        legacyContinuity.Nodes.AddRange(new[] { "a", "b", "c" }.Select(x => new NodeExperience
            { Scope = legacyContinuity.ActiveScope, Node = x }));
        Equal("legacy|chatgpt,gemini", legacyContinuity.ResolveScope("new", new[] { "a", "b", "c", "d" }, "chatgpt,gemini"),
            "legacy experience infers subscription continuity during upgrade");
        var jmMigration = new ExperienceData {
            ActiveScope = "old|ChatGPT,JMComicWeb,GitHub",
            ActiveServicesKey = "ChatGPT,JMComicWeb,GitHub",
            ActiveCandidateNames = new List<string> { "a", "b" },
            Assurance = new ConnectionAssurance { Scope = "old|ChatGPT,JMComicWeb,GitHub" }
        };
        jmMigration.Nodes.Add(new NodeExperience { Scope = jmMigration.ActiveScope, Node = "a" });
        string migratedScope = jmMigration.ResolveScope("new", new[] { "a", "b" }, "ChatGPT,GitHub");
        Equal("new|ChatGPT,GitHub", migratedScope, "JMComic removal rekeys but preserves remaining service history");
        Equal(migratedScope, jmMigration.Nodes[0].Scope, "JMComic migration keeps node history reachable");
        Equal(migratedScope, jmMigration.Assurance.Scope, "JMComic migration keeps assurance scope aligned");
        var memory = new ExperienceData();
        var scan = new CandidateScanResult("stable", CandidateHealth.Compatible, null, "ok", 100, 1);
        var partialMemory = new ExperienceData();
        partialMemory.Observe("scope", new CandidateScanResult("partial", CandidateHealth.BasicCompatible, null, "challenge", 100, 1), null, now);
        Equal(0, partialMemory.Nodes[0].SuccessfulSamples, "reachable-only evidence does not accumulate successful history");
        memory.Observe("scope", scan, null, now);
        memory.Observe("scope", scan, null, now.AddSeconds(1));
        Equal(1, memory.Nodes[0].Samples, "memory deduplicates same-cycle observations");
        memory.Observe("scope", scan, new QualitySample("stable", now.AddSeconds(2), true, 100, 0, 123456, 1), now.AddSeconds(2));
        Equal(123456.0, memory.Nodes[0].Throughput, "same-cycle throughput enriches response observation");
        Equal(false, memory.IsProvenStable("scope", "stable", now), "one-off success cannot authorize quality switch");
        Equal(0, memory.Recommend("scope", new[] { "stable" }, now).Count(), "memory does not recommend one-off success");
        memory.Observe("scope", scan, null, now.AddMinutes(5));
        memory.Observe("scope", scan, null, now.AddMinutes(10));
        Equal(false, memory.IsProvenStable("scope", "stable", now.AddMinutes(10)), "short history cannot authorize quality switch");
        Equal(1, memory.Recommend("scope", new[] { "stable" }, now.AddMinutes(10)).Count(), "repeated stable history becomes recommendation");
        memory.Observe("scope", scan, null, now.AddMinutes(20));
        memory.Observe("scope", scan, null, now.AddMinutes(30));
        Equal(true, memory.IsProvenStable("scope", "stable", now.AddMinutes(30)), "cross-time stable history authorizes quality switch");
        var ninetyPercent = new ExperienceData();
        var ninetyFivePercent = new ExperienceData();
        for (int i = 0; i < 20; i++)
        {
            DateTime checkedUtc = now.AddMinutes(i * 2);
            ninetyPercent.Observe("scope", new CandidateScanResult("ninety", i < 2 ? CandidateHealth.Transient : CandidateHealth.Compatible, null, "sample"), null, checkedUtc);
            ninetyFivePercent.Observe("scope", new CandidateScanResult("ninety-five", i == 0 ? CandidateHealth.Transient : CandidateHealth.Compatible, null, "sample"), null, checkedUtc);
        }
        Equal(false, ninetyPercent.IsProvenStable("scope", "ninety", now.AddMinutes(38)), "ninety percent history cannot authorize quality switch");
        Equal(true, ninetyFivePercent.IsProvenStable("scope", "ninety-five", now.AddMinutes(38)), "ninety-five percent history authorizes quality switch");
        var latencyMemory = new ExperienceData();
        for (int i = 1; i <= 6; i++)
            latencyMemory.Observe("scope", new CandidateScanResult("latency", CandidateHealth.Compatible, null, "ok", i * 100, 1), null, now.AddMinutes(i));
        Equal("200,300,400,500,600", String.Join(",", latencyMemory.RecentResponses("scope", "latency", 5).Select(x => x.ToString("F0"))), "recent response history keeps newest samples");
        Equal(true, latencyMemory.StabilitySummary("scope", "latency", now.AddMinutes(6)).Contains("6/5 次"), "stability summary exposes sample progress");
        Equal(0, memory.Recommend("different services", new[] { "stable" }, now.AddMinutes(10)).Count(), "memory isolated by service and subscription scope");
        Equal(0, memory.Recommend("scope", new[] { "removed" }, now.AddMinutes(10)).Count(), "removed node not recommended");
        Equal(0, memory.Recommend("scope", new[] { "stable" }, now.AddDays(8)).Count(), "stale history not recommended");
        memory.Observe("scope", new CandidateScanResult("stable", CandidateHealth.Transient, null, "timeout"), null, now.AddMinutes(31));
        Equal(0, memory.Recommend("scope", new[] { "stable" }, now.AddMinutes(31)).Count(), "latest failure overrides good memory");
        memory.RecordChange("stable", "stable", "same", now);
        Equal(0, memory.Changes.Count, "no false same-node history");
        for (int i = 0; i < 310; i++) memory.RecordChange("a", "b", "reason", now.AddSeconds(i));
        string path = Path.Combine(Path.GetTempPath(), "monitor-experience-" + Guid.NewGuid().ToString("N"), "experience.json");
        var store = new ExperienceStore(path);
        store.Save(memory, now.AddMinutes(32));
        var restored = store.Load();
        Equal(300, restored.Changes.Count, "switch history bounded and persisted");
        Equal(6, restored.Nodes[0].Samples, "experience survives restart");
        Equal("reason", restored.Changes[0].Reason, "switch reason survives restart");
        Equal("reason", restored.LatestSelectionReason("b"), "current selection reason exposed");
        Equal("启动时沿用 Clash 当前选择", restored.LatestSelectionReason("never-seen"), "startup selection source is explicit");
        Equal("开机沿用 Clash 上次节点；上次记录：reason", restored.SelectionSummary("b", true), "startup explains previous automatic selection");
        Equal("reason", restored.SelectionSummary("b", false), "live selection reason is not mislabeled as startup");
    }

    private static void CandidateFiltering()
    {
        var names = new[] {
            "DIRECT",
            "REJECT",
            "Tokyo-A",
            "节点 B | 0.5x",
            "🇭🇰 香港 I1 | IEPL | 3x",
            "🇸🇬 新加坡 M2 | BHE | 3x",
            "台湾-无倍率",
            "High cost | 5x",
            "Tokyo-A"
        };
        var candidates = CandidateCatalog.Filter(names);
        Equal(6, candidates.Count, "provider naming does not filter candidates");
        Equal("Tokyo-A", candidates[0].Name, "source ordering preserved");
        Equal(true, candidates.Any(x => x.Name.Contains("台湾")), "taiwan remains eligible");
        Equal(true, candidates.Any(x => x.Name == "Tokyo-A"), "plain provider name remains eligible");
        Equal(true, candidates.Any(x => x.Name.EndsWith("5x", StringComparison.Ordinal)), "high multiplier is measured not hidden");
        Equal<double?>(null, candidates.First(x => x.Name == "Tokyo-A").Multiplier, "missing multiplier stays unknown");
        Equal(0.5, candidates.First(x => x.Name.Contains("0.5x")).Multiplier.Value, "decimal multiplier retained");

        var runtimeTypes = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "Tokyo-A", "Shadowsocks" }, { "提示：流量剩余", "Direct" },
            { "机场自动选择", "Selector" }, { "节点 B | 0.5x", "Vless" }
        };
        var typed = CandidateCatalog.Filter(new[] { "Tokyo-A", "提示：流量剩余", "机场自动选择", "节点 B | 0.5x" }, runtimeTypes);
        Equal(2, typed.Count, "runtime proxy kinds exclude built-in and nested choices without relying on names");
        Equal("Tokyo-A", typed[0].Name, "typed filtering keeps source order");
        Equal(1, CandidateCatalog.Filter(new[] { "新协议节点" }, runtimeTypes).Count,
            "unknown runtime kind remains eligible for newer subscription protocols");
        var notices = new[] { "消息: 17条未读，在APP查看", "📢 公告：请更新订阅", "剩余流量：100GB", "套餐到期：2027-01-01", "Notice: subscription updated" };
        foreach (string notice in notices) runtimeTypes[notice] = "Trojan";
        Equal(0, CandidateCatalog.Filter(notices, runtimeTypes).Count,
            "subscription notice entries are excluded even with real proxy protocol metadata");
        var providerMetadata = new[] { "----- 联系我们 -----", "----- 账号信息 -----",
            "电报: https://t.me/example", "登录账号: user123", "豪华套餐: 剩余348天", "邮箱: support@example.com" };
        foreach (string notice in providerMetadata) runtimeTypes[notice] = "Trojan";
        Equal(0, CandidateCatalog.Filter(providerMetadata, runtimeTypes).Count,
            "provider contact and account metadata never enter node measurements");
        Equal(2, CandidateCatalog.Filter(new[] { "香港 I1 | 通知优化", "Tokyo-A" }, runtimeTypes).Count,
            "notice filtering does not reject node regions or ordinary words inside node names");
    }

    private static void PipeHttpDecoding()
    {
        string body = "{\"name\":\"节点\"}";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string chunked = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
            bodyBytes.Length.ToString("X") + "\r\n" + body + "\r\n0\r\n\r\n";
        var decoded = PipeHttpCodec.Decode(Encoding.UTF8.GetBytes(chunked));
        Equal(200, decoded.StatusCode, "chunked status");
        Equal(body, decoded.Body, "chunked utf8 body");

        string fixedResponse = "HTTP/1.1 201 Created\r\nContent-Length: " + bodyBytes.Length + "\r\n\r\n" + body;
        decoded = PipeHttpCodec.Decode(Encoding.UTF8.GetBytes(fixedResponse));
        Equal(201, decoded.StatusCode, "content length status");
        Equal(body, decoded.Body, "content length utf8 body");
    }

    private static void BoundedPipeBehavior()
    {
        var blocked = new BlockingDisposableStream();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Throws<TimeoutException>(() => BoundedPipeIo.ReadAll(blocked, TimeSpan.FromMilliseconds(80), 1024), "pipe read timeout");
        Equal(true, timer.ElapsedMilliseconds < 1000, "pipe timeout is bounded");
        Equal(true, blocked.Disposed, "timeout disposes blocked stream");
        Throws<InvalidDataException>(() => BoundedPipeIo.ReadAll(new MemoryStream(new byte[2048]), TimeSpan.FromSeconds(1), 1024), "pipe response cap");
    }

    private sealed class BlockingDisposableStream : Stream
    {
        private readonly ManualResetEventSlim released = new ManualResetEventSlim(false);
        public bool Disposed { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            released.Wait();
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            released.Set();
            base.Dispose(disposing);
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    private static void MihomoPipeIntegration()
    {
        string pipeName = "clash-monitor-test-" + Guid.NewGuid().ToString("N");
        var requests = new List<string>();
        Exception serverError = null;
        var server = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    using (var stream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1))
                    {
                        stream.WaitForConnection();
                        string request = ReadHttpRequest(stream);
                        lock (requests) requests.Add(request);
                        string responseBody = i == 0
                            ? "{\"proxies\":{\"组\":{\"type\":\"Selector\",\"now\":\"节点\",\"all\":[\"节点\",\"节点二\"]}}}"
                            : i == 1 ? "{\"proxies\":{\"节点\":{\"type\":\"Shadowsocks\"},\"说明\":{\"type\":\"Direct\"},\"新型组\":{\"type\":\"FutureGroup\",\"all\":[\"节点\"]},\"无类型组\":{\"all\":[\"节点\"]}}}"
                            : i == 3 ? "{\"delay\":86}" : i == 4 ? "{\"ipv6\":true}" : "";
                        byte[] payload = Encoding.UTF8.GetBytes(responseBody);
                        string response = i == 0
                            ? "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" + payload.Length.ToString("X") + "\r\n" + responseBody + "\r\n0\r\n\r\n"
                            : (i == 1 || i == 3 || i == 4) ? "HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length + "\r\n\r\n" + responseBody
                            : "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n";
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                }
            }
            catch (Exception ex) { serverError = ex; }
        });
        server.IsBackground = true;
        server.Start();

        var client = new MihomoPipeClient(pipeName, "test-secret");
        Equal("节点", client.GetSelected("组"), "pipe selected node");
        var proxyTypes = client.GetProxyTypes();
        Equal("Shadowsocks", proxyTypes["节点"], "mihomo exposes runtime leaf kind");
        Equal("Selector", proxyTypes["新型组"], "unknown kind with child choices is classified as non-leaf");
        Equal("Selector", proxyTypes["无类型组"], "missing kind with child choices is classified as non-leaf");
        Equal(1, CandidateCatalog.Filter(new[] { "节点", "新型组", "无类型组" }, proxyTypes).Count,
            "runtime group structure reaches candidate filtering");
        client.Select("组", "节点二");
        Equal(86, client.GetDelay("节点 一", "https://www.gstatic.com/generate_204", 5000), "mihomo delay");
        Equal(true, client.IsRuntimeIpv6Enabled(), "mihomo runtime ipv6 query");
        server.Join(5000);
        Equal(null, serverError, "fake pipe server error");
        Equal(5, requests.Count, "pipe request count");
        Equal(true, requests[0].StartsWith("GET /proxies HTTP/1.1\r\n", StringComparison.Ordinal), "pipe get path");
        Equal(true, requests[0].Contains("Authorization: Bearer test-secret"), "pipe auth header");
        Equal(true, requests[1].StartsWith("GET /proxies HTTP/1.1\r\n", StringComparison.Ordinal), "runtime kinds query path");
        Equal(true, requests[2].StartsWith("PUT /proxies/%E7%BB%84 HTTP/1.1\r\n", StringComparison.Ordinal), "pipe put escaped path");
        Equal(true, requests[2].Contains("{\"name\":\"节点二\"}"), "pipe put body");
        Equal(true, requests[3].StartsWith("GET /proxies/%E8%8A%82%E7%82%B9%20%E4%B8%80/delay?", StringComparison.Ordinal), "delay escaped path");
        Equal(true, requests[4].StartsWith("GET /configs HTTP/1.1\r\n", StringComparison.Ordinal), "runtime config query path");
    }

    private static string ReadHttpRequest(Stream stream)
    {
        var bytes = new List<byte>();
        int contentLength = 0;
        while (true)
        {
            int value = stream.ReadByte();
            if (value < 0) break;
            bytes.Add((byte)value);
            if (bytes.Count >= 4 && bytes[bytes.Count - 4] == 13 && bytes[bytes.Count - 3] == 10 && bytes[bytes.Count - 2] == 13 && bytes[bytes.Count - 1] == 10)
            {
                string headers = Encoding.ASCII.GetString(bytes.ToArray());
                foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.Substring(line.IndexOf(':') + 1).Trim());
                break;
            }
        }
        for (int i = 0; i < contentLength; i++) bytes.Add((byte)stream.ReadByte());
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void CompatibilityScanning()
    {
        Equal(ProbeFailureKind.Partial, ProbeResult.ReachableChallenge().FailureKind, "challenge is only basic reachability");
        Equal(ProbeFailureKind.Partial, HttpServiceProbe.EvaluateResponse(ServiceKind.ChatGPT, 200, "<html>ChatGPT</html>", null, 1).FailureKind, "homepage is basic reachability only");
        Equal(ProbeFailureKind.Partial, HttpServiceProbe.EvaluateResponse(ServiceKind.ChatGPT, 403, "cf-chl", null, 1).FailureKind, "403 challenge is basic reachability");
        Equal(ProbeFailureKind.Partial, HttpServiceProbe.EvaluateResponse(ServiceKind.ChatGPT, 403, "", null, 1, true).FailureKind, "challenge header is basic reachability");
        Equal(ProbeFailureKind.Region, HttpServiceProbe.EvaluateResponse(ServiceKind.ChatGPT, 403, "unsupported_country_region_territory", null, 1).FailureKind, "structured region error recognized");
        Equal(ProbeFailureKind.Partial, HttpServiceProbe.EvaluateResponse(ServiceKind.Gemini, 302, "", new Uri("https://accounts.google.com/ServiceLogin"), 1).FailureKind, "gemini login is basic reachability");
        Equal(ProbeFailureKind.Service, HttpServiceProbe.EvaluateResponse(ServiceKind.Gemini, 302, "", new Uri("https://accounts.google.com.evil.example/"), 1).FailureKind, "login host exact match");
        Equal("https://chatgpt.com/", HttpServiceProbe.Endpoint(ServiceKind.ChatGPT).AbsoluteUri, "application endpoint not trace");
        var basicProbe = new FakeProbe();
        basicProbe.Results[ServiceKind.ChatGPT] = ProbeResult.Partial("页面可达");
        basicProbe.Results[ServiceKind.Gemini] = ProbeResult.Partial("登录页可达");
        var basic = new CompatibilityScanner(new FakeMihomo(), basicProbe, "probe").Scan(new CandidateNode("basic", 1), null);
        Equal(CandidateHealth.Unknown, basic.Health,
            "AI reachability remains unverified when no exit identity probe is configured");
        Equal(7, basicProbe.Calls.Count, "basic reachability still checks all platforms");
        var pendingProbe = new FakeProbe();
        pendingProbe.Results[ServiceKind.ChatGPT] = ProbeResult.Unverified("无法确认");
        var pending = new CompatibilityScanner(new FakeMihomo(), pendingProbe, "probe").Scan(new CandidateNode("pending", 1), null);
        Equal(CandidateHealth.Unknown, pending.Health, "challenge is pending not failure");
        Equal(7, pendingProbe.Calls.Count, "pending continues other platform checks");
        Equal(true, pending.Detail.Contains("ChatGPT"), "pending retains platform reason");
        pendingProbe.Results[ServiceKind.Gemini] = ProbeResult.RegionFailure("blocked");
        pending = new CompatibilityScanner(new FakeMihomo(), pendingProbe, "probe").Scan(new CandidateNode("pending", 1), null);
        Equal(CandidateHealth.RegionBlocked, pending.Health, "definite failure overrides pending");
        Equal(125L, ProbeResult.Success(125).ElapsedMilliseconds, "probe elapsed");
        var mihomo = new FakeMihomo();
        var probe = new FakeProbe();
        probe.Results[ServiceKind.ChatGPT] = ProbeResult.ServiceFailure("blocked");
        var scanner = new CompatibilityScanner(mihomo, probe, "probe");
        var result = scanner.Scan(new CandidateNode("node", 1), new ServiceKind[0]);
        Equal(CandidateHealth.ServiceFailed, result.Health, "chatgpt failure classification");
        Equal(7, probe.Calls.Count, "chatgpt failure still completes concurrent service batch");

        probe = new FakeProbe();
        probe.Results[ServiceKind.Gemini] = ProbeResult.RegionFailure("unsupported");
        result = new CompatibilityScanner(mihomo, probe, "probe").Scan(new CandidateNode("node", 1), new ServiceKind[0]);
        Equal(CandidateHealth.RegionBlocked, result.Health, "gemini region classification");
        Equal(7, probe.Calls.Count, "gemini failure still completes concurrent service batch");

        probe = new FakeProbe();
        result = new CompatibilityScanner(mihomo, probe, "probe").Scan(new CandidateNode("node", 1), new[] { ServiceKind.Discord, ServiceKind.Spotify });
        Equal(CandidateHealth.Unknown, result.Health,
            "mandatory AI services remain unverified without an exit identity probe");
        Equal(9, probe.Calls.Count, "mandatory plus active optional count");
        Equal(false, probe.Calls.Contains(ServiceKind.Epic), "inactive optional skipped");
        Equal(4096, HttpServiceProbe.LimitBody(new byte[6000]).Length, "response body cap");
        Equal(4096, HttpServiceProbe.ReadLimited(new MemoryStream(new byte[6000]), 4096).Length, "stream body cap");
        Equal(120L, HttpServiceProbe.ResponseLatency(120, 900), "service latency uses response headers not body transfer");
        var bodyTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        bool bodyCanceled = false;
        var bodyTimer = System.Diagnostics.Stopwatch.StartNew();
        try { HttpServiceProbe.ReadLimitedAsync(new CancellationAwareStream(), 4096, bodyTimeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { bodyCanceled = true; }
        finally { bodyTimeout.Dispose(); }
        Equal(true, bodyCanceled, "body read obeys timeout");
        Equal(true, bodyTimer.ElapsedMilliseconds < 1000, "body timeout is bounded");
        Equal(true, HttpServiceProbe.IsChallengeResponse("challenge-platform"), "chatgpt challenge recognized");
        Equal(false, HttpServiceProbe.IsChallengeResponse("unsupported country"), "region block is not challenge");
        Equal("node", mihomo.LastSelected, "isolated selector changed");

        probe = new FakeProbe { DefaultResult = ProbeResult.Success(125) };
        result = new CompatibilityScanner(mihomo, probe, "probe").Scan(new CandidateNode("timed", 1), new ServiceKind[0]);
        Equal(875L, result.TotalMilliseconds, "scan total elapsed");
        Equal(7, result.ProbeCount, "mandatory probe count retained");
        Equal(125.0, QualityMeasurement.ResponseMilliseconds(result, 5000), "response uses service average");
        Equal(5000.0, QualityMeasurement.ResponseMilliseconds(
            new CandidateScanResult("none", CandidateHealth.Transient, null, "none", 0, 0), 5000),
            "empty scan uses fallback");
        var unevenResults = new Dictionary<ServiceKind, ProbeResult> {
            { ServiceKind.ChatGPT, ProbeResult.Partial("入口可达", 824) },
            { ServiceKind.Gemini, ProbeResult.Success(670) },
            { ServiceKind.Google, ProbeResult.Success(214) },
            { ServiceKind.GitHub, ProbeResult.Success(295) },
            { ServiceKind.SteamStore, ProbeResult.Success(322) },
            { ServiceKind.SteamCommunity, ProbeResult.Success(2569) },
            { ServiceKind.SteamApi, ProbeResult.Success(377) }
        };
        var unevenScan = new CandidateScanResult("uneven", CandidateHealth.BasicCompatible, null,
            "入口可达", 5271, 7, unevenResults);
        Equal(824.0, QualityMeasurement.ResponseMilliseconds(unevenScan, 5000),
            "response uses conservative nearest-rank p75 instead of average");
        string unevenLabel = MonitorPresentation.From(MonitorSnapshot.CreateRunning("uneven", unevenScan,
            null, "保持当前节点", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1))).ResponseText;
        Equal("综合响应：824 ms · 较慢 · Steam 社区 2569 ms", unevenLabel,
            "presentation exposes severe slow service instead of diluting it");

        var concurrentProbe = new ConcurrentEntryProbe(3);
        var concurrentTimer = System.Diagnostics.Stopwatch.StartNew();
        CandidateScanResult concurrent = new CompatibilityScanner(mihomo, concurrentProbe, "probe").ScanSelected(
            new CandidateNode("parallel", 1), new[] { ServiceKind.ChatGPT, ServiceKind.Gemini, ServiceKind.Google });
        concurrentTimer.Stop();
        Equal(CandidateHealth.Unknown, concurrent.Health,
            "concurrent AI probes still require a known exit identity");
        Equal(3, concurrent.ProbeCount, "concurrent scan retains every selected result");
        Equal(true, concurrentTimer.ElapsedMilliseconds < 450, "three bounded probes do not wait serially");

        probe = new FakeProbe { DefaultResult = ProbeResult.Success(100) };
        new CompatibilityScanner(mihomo, probe, "probe").ScanSelected(
            new CandidateNode("quick", 1), new[] { ServiceKind.Google }, TimeSpan.FromSeconds(2));
        Equal(TimeSpan.FromSeconds(2), probe.Timeouts.Single(), "fast candidate scan uses its shorter probe timeout");

        probe = new FakeProbe { DefaultResult = ProbeResult.Success(75) };
        DateTime observed = new DateTime(2026, 9, 21, 2, 0, 0, DateTimeKind.Utc);
        scanner = new CompatibilityScanner(mihomo, probe, "probe",
            new FakeExitIdentityProbe(new ExitIdentity("selected-fp", "JP", "ok")),
            new FakeClock { UtcNow = observed });
        CandidateScanResult selected = scanner.ScanSelected(new CandidateNode("selected", 1),
            new[] { ServiceKind.ChatGPT, ServiceKind.GitHub });
        Equal(2, probe.Calls.Count, "only selected services probed");
        Equal(75L, selected.ServiceResults[ServiceKind.ChatGPT].ElapsedMilliseconds, "service latency retained");
        Equal(2, selected.ServiceObservations.Count, "one observation per selected service");
        Equal(observed, selected.ServiceObservations[ServiceKind.GitHub].ObservedUtc,
            "scanner uses injected clock for observation time");
        Equal(ServiceOutcome.Success, selected.ServiceObservations[ServiceKind.ChatGPT].Outcome,
            "scanner records final service outcome");
        Equal("selected-fp", selected.ServiceObservations[ServiceKind.ChatGPT].ExitFingerprint,
            "scanner attaches exit fingerprint to raw observation");
        DateTime snapshotTime = new DateTime(2026, 9, 8, 8, 0, 0, DateTimeKind.Utc);
        MonitorSnapshot snapshot = MonitorSnapshot.CreateRunning("selected", selected, 82.5,
            "保持当前节点", snapshotTime, snapshotTime.AddMinutes(1));
        Equal("selected", snapshot.ActualNode, "snapshot leaf node");
        Equal(2, snapshot.Services.Count, "snapshot retains service evidence");
        MonitorPresentation view = MonitorPresentation.From(snapshot);
        Equal("运行正常", view.StateText, "login-ready AI scan is usable without a browser companion");
        Equal("网络链路兼容 · 真实对话待验证 · 75 ms",
            MonitorPresentation.ServiceText(snapshot.Services.First(x => x.Service == ServiceKind.ChatGPT)),
            "AI service distinguishes login chain from account proof");
        var accountData = new ExperienceData();
        AccountVerificationMemory.MarkBrowserConversation(accountData, "scope", "selected", "selected-fp",
            ServiceKind.ChatGPT, snapshotTime, BrowserConversationProof.CurrentProtocolVersion);
        snapshot = snapshot.WithAccountVerification(accountData, "scope", snapshotTime);
        view = MonitorPresentation.From(snapshot);
        Equal("运行正常", view.StateText, "account-verified AI scan is running normally");
        Equal("真实对话已验证 · 75 ms · 有效至 10-08",
            MonitorPresentation.ServiceText(snapshot.Services.First(x => x.Service == ServiceKind.ChatGPT)),
            "AI service shows account verification evidence");
        Equal("selected", view.NodeText, "presentation leaf node");
        Equal("综合响应：75 ms · 优秀", view.ResponseText, "presentation shows latency quality band");
        Equal("综合响应：待测", MonitorPresentation.From(MonitorSnapshot.CreateState(MonitorRunState.Starting, "", snapshotTime, snapshotTime)).ResponseText, "presentation handles missing latency");
        Equal("可用 · 75 ms", MonitorPresentation.ServiceText(true, 75, "ok"), "service latency label");
        Equal("不可用", MonitorPresentation.ServiceText(false, 75, "blocked"), "failure hides misleading latency");
        Equal("仅确认可达 · 75 ms", MonitorPresentation.ServiceText(new ServiceMeasurement(ServiceKind.ChatGPT, true, 75, "login", ProbeFailureKind.Partial)), "partial evidence is not full availability");
        Equal("待验证", MonitorPresentation.ServiceText(new ServiceMeasurement(ServiceKind.Gemini, false, 75, "pending", ProbeFailureKind.Unverified)), "unknown evidence is not failure");
        var preserved = snapshot.WithState(MonitorRunState.Checking, "checking", DateTime.MaxValue);
        Equal(snapshotTime, preserved.CheckedUtc, "progress preserves actual measurement time");
        Equal(2, preserved.Services.Count, "progress preserves service evidence");
        Equal("auto", snapshot.WithSelectionReason("auto").WithState(MonitorRunState.Checking, "checking", DateTime.MaxValue).SelectionReason, "progress preserves selection reason");
        Equal(MonitorRunState.Running, snapshot.WithProgress("后台准备备用节点").State, "healthy current remains ready during background maintenance");
        Equal("后台准备备用节点", snapshot.WithProgress("后台准备备用节点").Decision, "maintenance stage remains visible");
        Equal(MonitorRunState.Degraded, MonitorSnapshot.CreateRunning("x", new CandidateScanResult("x", CandidateHealth.Transient, null, "timeout"), null, "", snapshotTime, snapshotTime).State, "failed scan is not running normally");
        Equal(MonitorRunState.Pending, MonitorSnapshot.CreateRunning("x", new CandidateScanResult("x", CandidateHealth.Unknown, null, "pending"), null, "", snapshotTime, snapshotTime).State, "unknown scan is pending");
        Equal(MonitorRunState.Pending, MonitorSnapshot.CreateRunning("x", new CandidateScanResult("x", CandidateHealth.BasicCompatible, null, "challenge"), null, "", snapshotTime, snapshotTime).State, "reachable-only scan is pending not running normally");
        MonitorSnapshot basicProgress = MonitorSnapshot.CreateRunning("x",
            new CandidateScanResult("x", CandidateHealth.BasicCompatible, null, "challenge", 100, 1,
                new Dictionary<ServiceKind, ProbeResult> { { ServiceKind.ChatGPT, ProbeResult.Partial("入口可达", 100) } }),
            null, "", snapshotTime, snapshotTime).WithProgress("后台准备备用节点");
        Equal("基础连接可用", MonitorPresentation.From(basicProgress).StateText,
            "background maintenance does not hide completed basic reachability");
        Equal(true, StartupRecovery.NeedsImmediateConfirmation(new CandidateScanResult("x", CandidateHealth.Transient, ServiceKind.GitHub, "timeout")), "definite startup failure gets immediate confirmation");
        Equal(true, StartupRecovery.RequiresRepeatConfirmation(new CandidateScanResult("x", CandidateHealth.Transient, ServiceKind.GitHub, "timeout")), "ordinary timeout is quickly confirmed once");
        Equal(false, StartupRecovery.RequiresRepeatConfirmation(new CandidateScanResult("x", CandidateHealth.RegionBlocked, ServiceKind.ChatGPT, "unsupported country")), "explicit region rejection switches without retesting current node");
        Equal(false, StartupRecovery.RequiresRepeatConfirmation(new CandidateScanResult("x", CandidateHealth.ServiceFailed, ServiceKind.Gemini, "http rejection")), "explicit service rejection switches without retesting current node");
        var severeLatency = new CandidateScanResult("x", CandidateHealth.Compatible, null, "slow", 2300, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.Google, ProbeResult.Success(300) },
                { ServiceKind.GitHub, ProbeResult.Success(2001) }
            });
        Equal(ServiceKind.GitHub, StartupRecovery.FastFailoverService(severeLatency).Value,
            "one severely slow selected service enters fast replacement");
        Equal(true, StartupRecovery.RequiresRepeatConfirmation(severeLatency),
            "severe latency is confirmed once before candidate discovery");
        Equal(false, StartupRecovery.ConfirmsSevereLatency(
            new CandidateScanResult("x", CandidateHealth.Compatible, null, "recovered", 500, 1,
                new Dictionary<ServiceKind, ProbeResult> {
                    { ServiceKind.GitHub, ProbeResult.Success(500) }
                }), ServiceKind.GitHub),
            "recovered retry does not confirm severe latency");
        Equal(true, StartupRecovery.ConfirmsSevereLatency(
            new CandidateScanResult("x", CandidateHealth.Compatible, null, "still slow", 2100, 1,
                new Dictionary<ServiceKind, ProbeResult> {
                    { ServiceKind.GitHub, ProbeResult.Success(2100) }
                }), ServiceKind.GitHub),
            "repeated response above two seconds confirms severe latency");
        Equal(false, StartupRecovery.ConfirmsSevereLatency(
            new CandidateScanResult("x", CandidateHealth.Unknown, null, "unknown"),
            ServiceKind.GitHub),
            "unknown retry cannot manufacture confirmed degradation");
        var boundaryLatency = new CandidateScanResult("x", CandidateHealth.Compatible, null, "boundary", 2300, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.Google, ProbeResult.Success(300) },
                { ServiceKind.GitHub, ProbeResult.Success(2000) }
            });
        Equal(false, StartupRecovery.FastFailoverService(boundaryLatency).HasValue,
            "two second boundary does not churn the current node");
        var unknownSlow = new CandidateScanResult("x", CandidateHealth.Unknown, null, "pending", 2500, 1,
            new Dictionary<ServiceKind, ProbeResult> { { ServiceKind.GitHub, ProbeResult.Success(2500) } });
        Equal(false, StartupRecovery.FastFailoverService(unknownSlow).HasValue,
            "incomplete evidence cannot trigger repeated latency switching");
        var rankedRescue = StartupRecovery.RankFastCandidates(
            new[] { new CandidateNode("current", 1), new CandidateNode("slow", 1), new CandidateNode("fast", 1) },
            new Dictionary<string, int> { { "slow", 900 }, { "fast", 120 } }, "current", 2);
        Equal("fast", rankedRescue[0], "fast rescue checks the lowest live Mihomo delay first");
        Equal("slow", rankedRescue[1], "fast rescue keeps a bounded fallback");
        ServiceKind[] criticalServices = StartupRecovery.FastProbeServices(
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini, ServiceKind.Google, ServiceKind.GitHub,
                ServiceKind.SteamStore, ServiceKind.SteamCommunity, ServiceKind.SteamApi }, ServiceKind.SteamApi);
        Equal("ChatGPT,Gemini,Google,GitHub,SteamApi", String.Join(",", criticalServices),
            "fast comparison skips duplicate Steam endpoints but keeps the failed service");
        ServiceKind[] fixedCriticalServices = StartupRecovery.FastProbeServices(
            new[] { ServiceKind.Discord, ServiceKind.ZLibraryWeb }, ServiceKind.GitHub);
        Equal("ChatGPT,Gemini,Google,GitHub", String.Join(",", fixedCriticalServices),
            "fast comparison always checks the four region-safe critical services");
        ServiceKind[] fullFailoverServices = StartupRecovery.FullFailoverProbeServices(
            new[] { ServiceKind.Discord, ServiceKind.ZLibraryWeb }, ServiceKind.GitHub);
        Equal("ChatGPT,Gemini,Google,GitHub,Discord,ZLibraryWeb", String.Join(",", fullFailoverServices),
            "full failover validation retains every selected service after the critical gate");
        var fasterTarget = new CandidateScanResult("fast", CandidateHealth.BasicCompatible, null, "ok", 800, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.ChatGPT, ProbeResult.Partial("entry", 400) },
                { ServiceKind.GitHub, ProbeResult.Success(400) }
            }, 101L.ToString("X64"), "JP");
        var slowerTarget = new CandidateScanResult("slow", CandidateHealth.BasicCompatible, null, "ok", 1400, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.ChatGPT, ProbeResult.Partial("entry", 700) },
                { ServiceKind.GitHub, ProbeResult.Success(700) }
            }, 102L.ToString("X64"), "JP");
        string[] verifiedOrder = StartupRecovery.RankVerifiedFastTargets(
            new[] { slowerTarget, fasterTarget }, new Dictionary<string, int> { { "slow", 80 }, { "fast", 120 } },
            ServiceKind.ChatGPT);
        Equal("fast", verifiedOrder[0], "real service response outranks a lower synthetic delay");
        Equal(2, verifiedOrder.Length, "one or two eligible candidates are still ranked for failover");
        var unsupportedFastTarget = new CandidateScanResult("unsupported", CandidateHealth.BasicCompatible, null,
            "ok", 200, 1,
            new Dictionary<ServiceKind, ProbeResult> { { ServiceKind.ChatGPT, ProbeResult.Partial("entry", 200) } },
            103L.ToString("X64"), "HK");
        Equal(false, StartupRecovery.IsEligibleQuickScan(unsupportedFastTarget, ServiceKind.ChatGPT),
            "only actual shared-region candidates count as eligible");
        Equal(true, StartupRecovery.ShouldStopAfterEligibleCandidates(3),
            "fast selection stops after three eligible candidates");
        Equal(false, StartupRecovery.ShouldStopAfterEligibleCandidates(2),
            "fast selection continues with only two eligible candidates");
        Equal(false, StartupRecovery.ShouldStopAfterCheckedCandidates(7),
            "fast selection may inspect seven unsupported low-delay candidates");
        Equal(true, StartupRecovery.ShouldStopAfterCheckedCandidates(8),
            "fast selection checks at most eight low-delay candidates");
        Equal("实测优质节点", StartupRecovery.FastSelectionSummary(800),
            "800 ms target is labeled preferred");
        Equal("当前合格候选中延迟最低，但未达到 800 ms 优质标准",
            StartupRecovery.FastSelectionSummary(801),
            "over-800 ms target is not mislabeled as preferred");
        var middleTarget = new CandidateScanResult("middle", CandidateHealth.BasicCompatible, null, "ok", 1000, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.ChatGPT, ProbeResult.Partial("entry", 500) },
                { ServiceKind.GitHub, ProbeResult.Success(500) }
            }, 104L.ToString("X64"), "JP");
        string[] fullValidationOrder = StartupRecovery.RankVerifiedFastTargets(
            new[] { slowerTarget, middleTarget, fasterTarget },
            new Dictionary<string, int> { { "slow", 50 }, { "middle", 70 }, { "fast", 90 } },
            ServiceKind.ChatGPT);
        Equal("fast,middle,slow", String.Join(",", fullValidationOrder),
            "full validation ranks every fallback after the winner");
        var attemptedFullTargets = new HashSet<string>(StringComparer.Ordinal);
        string firstFullTarget = StartupRecovery.NextFullValidationTarget(fullValidationOrder, attemptedFullTargets);
        attemptedFullTargets.Add(firstFullTarget);
        string secondFullTarget = StartupRecovery.NextFullValidationTarget(fullValidationOrder, attemptedFullTargets);
        Equal("fast", firstFullTarget, "full validation starts with the real-service winner");
        Equal("middle", secondFullTarget, "failed winner advances to the second ranked target");
        Equal(false, StartupRecovery.ShouldStopFastComparison(5, 0, 5000), "comparison expands when the first five have no usable target");
        Equal(false, StartupRecovery.ShouldStopFastComparison(5, 2, 1200), "comparison keeps looking when two targets are merely usable");
        Equal(true, StartupRecovery.ShouldStopFastComparison(5, 2, 700), "comparison stops after two targets include a genuinely fast option");
        Equal(true, StartupRecovery.ShouldStopFastComparison(12, 0, 5000), "comparison remains bounded when the subscription has no usable target");
        string[] orderedFastCandidates = StartupRecovery.OrderFastCandidates(
            new[] { "live-fast", "live-second" }, new[] { "stale-standby", "live-fast" },
            new[] { "old-history" }, new[] { "old-experience" },
            new[] { "current", "last-resort" }, "current", 5);
        Equal("live-fast,live-second,stale-standby,old-history,old-experience", String.Join(",", orderedFastCandidates),
            "fresh live latency order outranks stale standby and history");
        var firstTimeout = new CandidateScanResult("x", CandidateHealth.Transient, ServiceKind.SteamApi, "timeout", 5200, 2,
            new Dictionary<ServiceKind, ProbeResult>
            {
                { ServiceKind.Google, ProbeResult.Success(200) },
                { ServiceKind.SteamApi, ProbeResult.TransientFailure("timeout", 5000) }
            });
        var retryPassed = new CandidateScanResult("x", CandidateHealth.Compatible, null, "ok", 300, 1,
            new Dictionary<ServiceKind, ProbeResult> { { ServiceKind.SteamApi, ProbeResult.Success(300) } });
        CandidateScanResult recovered = StartupRecovery.ApplySuccessfulConfirmation(firstTimeout, retryPassed, ServiceKind.SteamApi);
        Equal(CandidateHealth.Compatible, recovered.Health, "successful timeout retry repairs the current cycle");
        Equal(false, recovered.FailedService.HasValue, "successful timeout retry clears the stale failure");
        Equal(300L, recovered.ServiceResults[ServiceKind.SteamApi].ElapsedMilliseconds,
            "successful timeout retry replaces the failed service evidence");
        Equal(false, StartupRecovery.NeedsImmediateConfirmation(new CandidateScanResult("x", CandidateHealth.Unknown, null, "pending")), "unknown startup evidence is not forced failure");
        Equal(4, StartupRecovery.MaximumCandidates(new CandidateScanResult("x", CandidateHealth.Transient, ServiceKind.GitHub, "timeout")), "startup failure search is bounded");
        Equal(3, StartupRecovery.MaximumCandidates(selected), "healthy maintenance search stays small");
        Equal(false, StartupRecovery.RequiresFinalRecheck(TimeSpan.FromSeconds(10)), "fresh complete scan is accepted for switch");
        Equal(true, StartupRecovery.RequiresFinalRecheck(TimeSpan.FromSeconds(16)), "older scan is rechecked before switch");
        scanner.ShouldStop = () => true;
        int beforeStop = probe.Calls.Count;
        Throws<OperationCanceledException>(() => scanner.ScanSelected(new CandidateNode("cancelled", 1), new[] { ServiceKind.Google }), "cancelled scan exits before probe");
        Equal(beforeStop, probe.Calls.Count, "cancelled scan sends no requests");
    }

    private static void FastFailoverWorkerOrchestration()
    {
        int originalWorkerThreads;
        int originalIoThreads;
        ThreadPool.GetMinThreads(out originalWorkerThreads, out originalIoThreads);
        ThreadPool.SetMinThreads(Math.Max(originalWorkerThreads, 32), Math.Max(originalIoThreads, 32));
        try
        {
            RunRecoveredSevereLatencyOrchestration();
            RunConfirmedSevereLatencyOrchestration();
            RunUnhelpfulSevereLatencyOrchestration();
            RunBudgetedSevereLatencyOrchestration();
            RunEligibleFastFailoverOrchestration();
            RunEightCandidateFastFailoverBound();
            RunAutomaticOpportunityOrchestration();
            RunRecentSwitchOpportunityHysteresisOrchestration();
            RunBudgetedOpportunityConfirmationOrchestration();
            RunActiveCircuitAutomaticFailover();
            RunConsensusCircuitDoesNotAttributeNodeFailure();
            RunCircuitRollbackRechecksAllRequiredServices();
        }
        finally
        {
            ThreadPool.SetMinThreads(originalWorkerThreads, originalIoThreads);
        }
    }

    private static void RunRecoveredSevereLatencyOrchestration()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-latency-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] alternatives = Enumerable.Range(1, 3)
                .Select(x => "node-" + x.ToString("D2")).ToArray();
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current",
                new[] { "current" }.Concat(alternatives), delays);
            var probe = new SevereLatencyProbe(mihomo, 500);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo, probe,
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = new DateTime(2026, 9, 20, 16, 0, 0, DateTimeKind.Utc) },
                new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.RunOnce(false, UserPreferences.Defaults());

            Equal(2, probe.CurrentChatGptCalls,
                "severe latency receives one focused current-node retry");
            Equal(0, mihomo.DelayNodes(2500).Count,
                "recovered retry avoids the all-node delay round");
            Equal("current", mihomo.GetSelected("shared"),
                "recovered retry keeps the current node");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void RunConfirmedSevereLatencyOrchestration()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-latency-confirmed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] alternatives = Enumerable.Range(1, 3)
                .Select(x => "node-" + x.ToString("D2")).ToArray();
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current",
                new[] { "current" }.Concat(alternatives), delays);
            var probe = new SevereLatencyProbe(mihomo, 2100);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo, probe,
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = new DateTime(2026, 9, 20, 16, 5, 0, DateTimeKind.Utc) },
                new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.RunOnce(false, UserPreferences.Defaults());

            Equal(2, probe.CurrentChatGptCalls,
                "repeated severe latency performs one focused confirmation");
            Equal(alternatives.Length, mihomo.DelayNodes(2500).Count,
                "confirmed severe latency performs one all-node delay round");
            Equal(alternatives.Length,
                mihomo.DelayNodes(2500).Distinct(StringComparer.Ordinal).Count(),
                "confirmed severe latency never repeats the all-node delay round");
            Equal("node-01", mihomo.GetSelected("shared"),
                "confirmed severe latency may switch after complete target validation");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void RunAutomaticOpportunityOrchestration()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-opportunity-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
            string[] alternatives = Enumerable.Range(1, 7).Select(x => "node-" + x.ToString("D2")).ToArray();
            string[] choices = new[] { "current" }.Concat(alternatives).ToArray();
            var preferences = UserPreferences.Defaults();
            preferences.AutomaticOptimization = true;
            string servicesKey = String.Join(",", preferences.RequiredServices.Distinct().OrderBy(x => x));
            string scope = WorkerScope(choices, servicesKey);
            var persisted = new ExperienceData {
                ActiveScope = scope,
                ActiveServicesKey = servicesKey,
                ActiveCandidateNames = choices.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                LastNode = "current",
                Assurance = new ConnectionAssurance { Scope = scope, Standbys = new List<StandbyNode>() },
                Nodes = new List<NodeExperience> {
                    new NodeExperience { Scope = scope, Node = "current", FirstUtc = now.AddMinutes(-3),
                        LastUtc = now.AddMinutes(-1), Samples = 3, Success = 1, LastPassed = true,
                        RecentResponseMilliseconds = new List<double> { 1000, 1000, 1000 } },
                    new NodeExperience { Scope = scope, Node = "node-06", FirstUtc = now.AddHours(-1),
                        LastUtc = now.AddMinutes(-1), Samples = 6, Success = 1, LastPassed = true,
                        ResponseMs = 400, RecentResponseMilliseconds = new List<double> { 400, 410, 390 } }
                }
            };
            new ExperienceStore(Path.Combine(root, "state", "experience.json")).Save(persisted, now);
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current", choices, delays);
            var clock = new FakeClock { UtcNow = now };
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo,
                new OpportunityServiceProbe(mihomo),
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024), clock,
                new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            MonitorSnapshot discovery = worker.Run(preferences);

            Equal(alternatives.Length, mihomo.DelayNodes(2500).Distinct(StringComparer.Ordinal).Count(),
                "scheduled opportunity scan measures every alternative once");
            Equal("node-01,node-06,node-07", String.Join(",", mihomo.ScanNodes(TimeSpan.FromSeconds(2))),
                "scheduled opportunity scan includes live historical and exploration candidates before stopping");
            Equal("current", mihomo.GetSelected("shared"),
                "first opportunity scan prepares a target without switching");
            ExperienceData afterDiscovery = new ExperienceStore(Path.Combine(root, "state", "experience.json")).Load();
            Equal("node-01", afterDiscovery.Assurance.PendingOptimization.Target,
                "fresh service response outranks historical candidate origin");
            Equal(AutomaticDecisionState.ConfirmingOptimization, afterDiscovery.Assurance.Decision.State,
                "opportunity discovery enters optimization confirmation state");
            Equal(TimeSpan.FromSeconds(30), discovery.NextCheckUtc - discovery.CheckedUtc,
                "pending target schedules a thirty-second confirmation");
            string trace = File.ReadAllText(Path.Combine(root, "logs", "monitor.log"));
            Equal(true, trace.Contains("opportunity candidate plan live=5 historical=1 exploration=1 fill=0"),
                "opportunity trace records source counts");
            Equal(true, trace.Contains("source=historical") && trace.Contains("source=exploration"),
                "opportunity trace records scanned source categories");
            Equal(false, trace.Contains("node-06") || trace.Contains("node-07"),
                "opportunity trace never exposes raw node names");

            int delayCount = mihomo.DelayNodes(2500).Count;
            clock.UtcNow = now.AddSeconds(30);
            MonitorSnapshot confirmation = worker.Run(preferences);

            Equal(delayCount, mihomo.DelayNodes(2500).Count,
                "confirmation performs no second all-node delay round");
            Equal("node-01", mihomo.GetSelected("shared"),
                "automatic confirmation switches the materially faster target");
            ExperienceData afterConfirmation = new ExperienceStore(Path.Combine(root, "state", "experience.json")).Load();
            Equal("node-01", afterConfirmation.Assurance.Target,
                "automatic optimization enters normal post-switch observation");
            Equal(AutomaticDecisionState.Observing, afterConfirmation.Assurance.Decision.State,
                "confirmed optimization enters state-machine observation");
            Equal<PendingOptimization>(null, afterConfirmation.Assurance.PendingOptimization,
                "successful confirmation clears pending optimization");
            Equal(TimeSpan.FromSeconds(30), confirmation.NextCheckUtc - confirmation.CheckedUtc,
                "automatic switch keeps the observation interval");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

private static void RunRecentSwitchOpportunityHysteresisOrchestration()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-opportunity-hysteresis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = new DateTime(2026, 9, 20, 13, 0, 0, DateTimeKind.Utc);
            string[] alternatives = Enumerable.Range(1, 3).Select(x => "node-" + x.ToString("D2")).ToArray();
            string[] choices = new[] { "current" }.Concat(alternatives).ToArray();
            var preferences = UserPreferences.Defaults();
            preferences.AutomaticOptimization = true;
            string servicesKey = String.Join(",", preferences.RequiredServices.Distinct().OrderBy(x => x));
            string scope = WorkerScope(choices, servicesKey);
            var persisted = new ExperienceData {
                ActiveScope = scope,
                ActiveServicesKey = servicesKey,
                ActiveCandidateNames = choices.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                LastNode = "current",
                Assurance = new ConnectionAssurance {
                    Scope = scope,
                    Standbys = new List<StandbyNode>(),
                    AutomaticSwitches = new List<AutomaticSwitchRecord> {
                        new AutomaticSwitchRecord { Utc = now.AddMinutes(-5), From = "old", To = "current" }
                    }
                },
                Nodes = new List<NodeExperience> {
                    new NodeExperience { Scope = scope, Node = "current", FirstUtc = now.AddMinutes(-3),
                        LastUtc = now.AddMinutes(-1), Samples = 3, Success = 1, LastPassed = true,
                        RecentResponseMilliseconds = new List<double> { 1000, 1000, 1000 } }
                }
            };
            new ExperienceStore(Path.Combine(root, "state", "experience.json")).Save(persisted, now);
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current", choices, delays);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo,
                new OpportunityServiceProbe(mihomo, 1000, 750),
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = now }, new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.Run(preferences);

            ExperienceData after = new ExperienceStore(Path.Combine(root, "state", "experience.json")).Load();
            Equal<PendingOptimization>(null, after.Assurance.PendingOptimization,
                "recent switch rejects an optimization below thirty percent improvement");
            Equal("current", mihomo.GetSelected("shared"),
                "recent switch hysteresis keeps the current node");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

private static void RunBudgetedOpportunityConfirmationOrchestration()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-opportunity-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = new DateTime(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc);
            string[] alternatives = new[] { "node-01", "node-02", "node-03" };
            string[] choices = new[] { "current" }.Concat(alternatives).ToArray();
            var preferences = UserPreferences.Defaults();
            preferences.AutomaticOptimization = true;
            string servicesKey = String.Join(",", preferences.RequiredServices.Distinct().OrderBy(x => x));
            string scope = WorkerScope(choices, servicesKey);
            var pending = new PendingOptimization { Scope = scope, Current = "current", Target = "node-01",
                BaselineResponse = 1000, TargetResponse = 150, CreatedUtc = now.AddSeconds(-30) };
            var persisted = new ExperienceData {
                ActiveScope = scope,
                ActiveServicesKey = servicesKey,
                ActiveCandidateNames = choices.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                LastNode = "current",
                Assurance = new ConnectionAssurance {
                    Scope = scope,
                    PendingOptimization = pending,
                    Decision = new AutomaticDecisionTransaction {
                        State = AutomaticDecisionState.ConfirmingOptimization,
                        Scope = scope, Current = "current", Target = "node-01",
                        BaselineResponse = 1000, TargetResponse = 150,
                        StartedUtc = now.AddSeconds(-30), Revision = 2
                    },
                    AutomaticSwitches = new List<AutomaticSwitchRecord> {
                        new AutomaticSwitchRecord { Utc = now.AddMinutes(-9), From = "a", To = "b" },
                        new AutomaticSwitchRecord { Utc = now.AddMinutes(-1), From = "b", To = "current" }
                    }
                },
                Nodes = new List<NodeExperience> {
                    new NodeExperience { Scope = scope, Node = "current", FirstUtc = now.AddMinutes(-3),
                        LastUtc = now.AddMinutes(-1), Samples = 3, Success = 1, LastPassed = true,
                        RecentResponseMilliseconds = new List<double> { 1000, 1000, 1000 } }
                }
            };
            new ExperienceStore(Path.Combine(root, "state", "experience.json")).Save(persisted, now);
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current", choices, delays);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo,
                new OpportunityServiceProbe(mihomo),
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = now }, new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.Run(preferences);

            ExperienceData after = new ExperienceStore(Path.Combine(root, "state", "experience.json")).Load();
            Equal("current", mihomo.GetSelected("shared"),
                "optimization confirmation respects the automatic switch budget");
            Equal(AutomaticDecisionState.Stabilization, after.Assurance.Decision.State,
                "budgeted optimization enters stabilization");
            Equal<PendingOptimization>(null, after.Assurance.PendingOptimization,
                "budgeted optimization clears its pending target");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void OpportunityOptimizationBehavior()
    {
        DateTime now = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        Equal(true, OpportunityOptimizationPolicy.ShouldScan(true, false,
            new[] { 900d, 850d, 801d }, DateTime.MinValue, now),
            "slow current schedules automatic opportunity discovery");
        Equal(false, OpportunityOptimizationPolicy.ShouldScan(true, false,
            new[] { 900d, 800d, 700d }, DateTime.MinValue, now),
            "good current does not schedule opportunity discovery");
        Equal(false, OpportunityOptimizationPolicy.ShouldScan(true, false,
            new[] { 900d, 850d, 801d }, now.AddMinutes(-2), now),
            "opportunity discovery is limited to one round per three minutes");
        Equal(true, OpportunityOptimizationPolicy.MateriallyBetter(1000, 800),
            "twenty percent faster target is material");
        Equal(false, OpportunityOptimizationPolicy.MateriallyBetter(1000, 801),
            "less than twenty percent is not material");
        var pending = new PendingOptimization { Scope = "scope", Current = "current", Target = "target",
            CreatedUtc = now.AddSeconds(-30) };
        Equal(true, OpportunityOptimizationPolicy.IsFresh(pending, "scope", "current", now),
            "matching pending target is fresh during confirmation window");
        Equal(false, OpportunityOptimizationPolicy.IsFresh(pending, "scope", "manual", now),
            "external selector change invalidates pending target");
        Equal(false, OpportunityOptimizationPolicy.IsFresh(pending, "new-scope", "current", now),
            "scope change invalidates pending target");
        pending.CreatedUtc = now.AddMinutes(-2);
        Equal(true, OpportunityOptimizationPolicy.IsFresh(pending, "scope", "current", now),
            "pending target remains fresh at two-minute boundary");
        pending.CreatedUtc = now.AddMinutes(-2).AddTicks(-1);
        Equal(false, OpportunityOptimizationPolicy.IsFresh(pending, "scope", "current", now),
            "pending target expires beyond two minutes");

        var required = new[] { ServiceKind.ChatGPT, ServiceKind.Gemini, ServiceKind.Google };
        var basic = new CandidateScanResult("basic", CandidateHealth.BasicCompatible, null, "reachable", 600, 3,
            new Dictionary<ServiceKind, ProbeResult> {
                { ServiceKind.ChatGPT, ProbeResult.Partial("entry reachable", 600) },
                { ServiceKind.Gemini, ProbeResult.Partial("entry reachable", 500) },
                { ServiceKind.Google, ProbeResult.Success(200) }
            }, "0000000000000000000000000000000000000000000000000000000000000001", "JP");
        Equal(true, OpportunityOptimizationPolicy.IsPerformanceComparable(basic, required),
            "supported basic-compatible evidence can participate in proactive comparison");
        Equal(false, OpportunityOptimizationPolicy.IsPerformanceComparable(basic, required.Concat(new[] { ServiceKind.GitHub })),
            "proactive comparison requires every selected service");
        var unsupported = new CandidateScanResult("hk", CandidateHealth.BasicCompatible, null, "reachable", 600, 3,
            basic.ServiceResults, "0000000000000000000000000000000000000000000000000000000000000002", "HK");
        Equal(false, OpportunityOptimizationPolicy.IsPerformanceComparable(unsupported, required),
            "unsupported actual exit cannot participate in proactive comparison");
    }

    private static void RunUnhelpfulSevereLatencyOrchestration()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-latency-unhelpful-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] alternatives = Enumerable.Range(1, 3)
                .Select(x => "node-" + x.ToString("D2")).ToArray();
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current",
                new[] { "current" }.Concat(alternatives), delays);
            var probe = new SevereLatencyProbe(mihomo, 2100, 1950);
            DateTime now = new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo, probe,
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = now },
                new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.RunOnce(false, UserPreferences.Defaults());

            Equal("current", mihomo.GetSelected("shared"),
                "severe degradation keeps current when absolute improvement is too small");
            ExperienceData persisted = new ExperienceStore(Path.Combine(root, "state", "experience.json")).Load();
            Equal(AutomaticDecisionState.Degraded, persisted.Assurance.Decision.State,
                "rejected severe degradation ends in degraded state");
            string log = File.ReadAllText(Path.Combine(root, "logs", "monitor.log"));
            Equal(true, log.Contains("state_from=") && log.Contains("state_to=") &&
                log.Contains("event=") && log.Contains("evidence=") &&
                log.Contains("directive=") && log.Contains("switches_10m=") &&
                log.Contains("switches_30m="),
                "decision trace records bounded transition fields");
            Equal(true, log.Contains("required_relative=") &&
                log.Contains("required_absolute_ms=") && log.Contains("accepted=false"),
                "decision trace records active improvement thresholds and rejection");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void RunBudgetedSevereLatencyOrchestration()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-latency-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = new DateTime(2026, 9, 21, 9, 10, 0, DateTimeKind.Utc);
            var persisted = new ExperienceData {
                Assurance = new ConnectionAssurance {
                    AutomaticSwitches = new List<AutomaticSwitchRecord> {
                        new AutomaticSwitchRecord { Utc = now.AddMinutes(-9), From = "a", To = "b" },
                        new AutomaticSwitchRecord { Utc = now.AddMinutes(-1), From = "b", To = "current" }
                    }
                }
            };
            new ExperienceStore(Path.Combine(root, "state", "experience.json")).Save(persisted, now);
            string[] alternatives = Enumerable.Range(1, 3)
                .Select(x => "node-" + x.ToString("D2")).ToArray();
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current",
                new[] { "current" }.Concat(alternatives), delays);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo,
                new SevereLatencyProbe(mihomo, 2100, 300),
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = now },
                new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.RunOnce(false, UserPreferences.Defaults());

            Equal("current", mihomo.GetSelected("shared"),
                "switch budget blocks severe degradation churn");
            ExperienceData after = new ExperienceStore(Path.Combine(root, "state", "experience.json")).Load();
            Equal(AutomaticDecisionState.Stabilization, after.Assurance.Decision.State,
                "exhausted switch budget enters stabilization");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void AutomaticDecisionPolicyBehavior()
    {
        var regionBlocked = new CandidateScanResult("current", CandidateHealth.RegionBlocked,
            ServiceKind.ChatGPT, "unsupported", 100, 1,
            new Dictionary<ServiceKind, ProbeResult> {
                { ServiceKind.ChatGPT, ProbeResult.RegionFailure("unsupported", 100) }
            });
        var serviceFailed = new CandidateScanResult("current", CandidateHealth.ServiceFailed,
            ServiceKind.Gemini, "rejected", 100, 1,
            new Dictionary<ServiceKind, ProbeResult> {
                { ServiceKind.Gemini, ProbeResult.ServiceFailure("rejected", 100) }
            });
        var stillSlowButPassed = new CandidateScanResult("current", CandidateHealth.Compatible,
            null, "slow", 2100, 1,
            new Dictionary<ServiceKind, ProbeResult> {
                { ServiceKind.ChatGPT, ProbeResult.Success(2100) }
            });
        var unknown = new CandidateScanResult("current", CandidateHealth.Unknown, null, "unknown");
        var healthy = new CandidateScanResult("current", CandidateHealth.Compatible, null, "ok", 500, 1,
            new Dictionary<ServiceKind, ProbeResult> {
                { ServiceKind.ChatGPT, ProbeResult.Success(500) }
            });
        var normallySlow = new CandidateScanResult("current", CandidateHealth.Compatible, null, "slow", 900, 1,
            new Dictionary<ServiceKind, ProbeResult> {
                { ServiceKind.ChatGPT, ProbeResult.Success(900) }
            });

        Equal(DecisionEvidenceClass.HardFailure,
            DecisionEvidencePolicy.Classify(regionBlocked, false),
            "region rejection is a hard failure");
        Equal(DecisionEvidenceClass.HardFailure,
            DecisionEvidencePolicy.Classify(serviceFailed, false),
            "definite service rejection is a hard failure");
        Equal(DecisionEvidenceClass.SevereDegradation,
            DecisionEvidencePolicy.Classify(stillSlowButPassed, true),
            "confirmed two-second latency is degradation not failure");
        Equal(DecisionEvidenceClass.Unknown,
            DecisionEvidencePolicy.Classify(unknown, false),
            "missing evidence remains unknown");
        Equal(DecisionEvidenceClass.Healthy,
            DecisionEvidencePolicy.Classify(healthy, false),
            "usable bounded evidence is healthy");
        Equal(DecisionEvidenceClass.NormalDegradation,
            DecisionEvidencePolicy.Classify(normallySlow, false),
            "usable response above the experience target is normal degradation");

        Equal(false, MaterialImprovementPolicy.Evaluate(200, 150, false).Accepted,
            "small absolute gain cannot switch despite relative gain");
        Equal(true, MaterialImprovementPolicy.Evaluate(1200, 800, false).Accepted,
            "normal improvement requires twenty percent and two hundred milliseconds");
        Equal(false, MaterialImprovementPolicy.Evaluate(1200, 850, true).Accepted,
            "recent switch raises relative requirement to thirty percent");
        Equal(true, MaterialImprovementPolicy.Evaluate(1200, 800, true).Accepted,
            "recent switch accepts a thirty-percent material improvement");
    }

    private static void AutomaticDecisionStateMachineBehavior()
    {
        DateTime now = new DateTime(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc);
        var current = new AutomaticDecisionTransaction {
            State = AutomaticDecisionState.Healthy,
            Scope = "scope",
            Current = "current",
            StartedUtc = now
        };
        DecisionTransition transition = AutomaticDecisionStateMachine.Transition(current,
            AutomaticDecisionEvent.NormalDegradation,
            new AutomaticDecisionContext { NowUtc = now, Evidence = DecisionEvidenceClass.NormalDegradation });
        Equal(AutomaticDecisionState.Degraded, transition.Transaction.State,
            "normal degradation enters degraded state");
        Equal(AutomaticDecisionDirective.None, transition.Directive,
            "normal degradation does not start an automatic transaction");

        transition = AutomaticDecisionStateMachine.Transition(current,
            AutomaticDecisionEvent.HardFailure,
            new AutomaticDecisionContext { NowUtc = now, Evidence = DecisionEvidenceClass.HardFailure,
                Service = ServiceKind.ChatGPT, Reason = "region blocked" });
        Equal(AutomaticDecisionState.ConfirmingFailure, transition.Transaction.State,
            "hard failure preempts healthy state");
        Equal(AutomaticDecisionDirective.ConfirmCurrent, transition.Directive,
            "hard failure requests focused confirmation");

        transition = AutomaticDecisionStateMachine.Transition(transition.Transaction,
            AutomaticDecisionEvent.FailureConfirmed,
            new AutomaticDecisionContext { NowUtc = now.AddSeconds(1),
                Evidence = DecisionEvidenceClass.HardFailure });
        Equal(AutomaticDecisionState.Recovering, transition.Transaction.State,
            "confirmed hard failure enters recovery");
        Equal(AutomaticDecisionDirective.SearchRecovery, transition.Directive,
            "confirmed hard failure requests candidate recovery");

        var optimization = new AutomaticDecisionTransaction {
            State = AutomaticDecisionState.SearchingOptimization,
            Scope = "scope", Current = "current", StartedUtc = now
        };
        transition = AutomaticDecisionStateMachine.Transition(optimization,
            AutomaticDecisionEvent.OptimizationTargetPrepared,
            new AutomaticDecisionContext { NowUtc = now, Target = "target", BaselineResponse = 1200,
                TargetResponse = 700, Reason = "candidate ready" });
        Equal(AutomaticDecisionState.ConfirmingOptimization, transition.Transaction.State,
            "prepared optimization target enters confirmation");
        Equal(AutomaticDecisionDirective.ConfirmOptimization, transition.Directive,
            "prepared target requests delayed confirmation");
        transition = AutomaticDecisionStateMachine.Transition(transition.Transaction,
            AutomaticDecisionEvent.HardFailure,
            new AutomaticDecisionContext { NowUtc = now.AddSeconds(2),
                Evidence = DecisionEvidenceClass.HardFailure, Service = ServiceKind.Gemini });
        Equal(AutomaticDecisionState.ConfirmingFailure, transition.Transaction.State,
            "hard failure cancels pending optimization");
        Equal<string>(null, transition.Transaction.Target,
            "hard failure clears stale optimization target");

        var switching = new AutomaticDecisionTransaction {
            State = AutomaticDecisionState.Switching, Current = "current", Target = "target",
            Previous = "current", StartedUtc = now
        };
        transition = AutomaticDecisionStateMachine.Transition(switching,
            AutomaticDecisionEvent.SwitchSucceeded,
            new AutomaticDecisionContext { NowUtc = now.AddSeconds(3) });
        Equal(AutomaticDecisionState.Observing, transition.Transaction.State,
            "successful switch enters observation");
        Equal(AutomaticDecisionDirective.Observe, transition.Directive,
            "successful switch requests observation");
        transition = AutomaticDecisionStateMachine.Transition(transition.Transaction,
            AutomaticDecisionEvent.ObservationComplete,
            new AutomaticDecisionContext { NowUtc = now.AddMinutes(2),
                ExpiresUtc = now.AddMinutes(32) });
        Equal(AutomaticDecisionState.Cooldown, transition.Transaction.State,
            "completed observation enters cooldown");
        transition = AutomaticDecisionStateMachine.Transition(transition.Transaction,
            AutomaticDecisionEvent.CooldownExpired,
            new AutomaticDecisionContext { NowUtc = now.AddMinutes(32) });
        Equal(AutomaticDecisionState.Healthy, transition.Transaction.State,
            "expired cooldown returns to healthy state");

        transition = AutomaticDecisionStateMachine.Transition(optimization,
            AutomaticDecisionEvent.ManualNodeChanged,
            new AutomaticDecisionContext { NowUtc = now, Current = "manual" });
        Equal(AutomaticDecisionState.Cooldown, transition.Transaction.State,
            "manual selection cancels automatic transaction into cooldown");
        Equal(AutomaticDecisionDirective.Cancel, transition.Directive,
            "manual selection emits cancellation directive");
        Equal<string>(null, transition.Transaction.Target,
            "manual selection clears pending target");

        var stabilizing = new AutomaticDecisionTransaction {
            State = AutomaticDecisionState.Stabilization, Current = "current",
            ExpiresUtc = now.AddMinutes(5)
        };
        transition = AutomaticDecisionStateMachine.Transition(stabilizing,
            AutomaticDecisionEvent.HardFailure,
            new AutomaticDecisionContext { NowUtc = now,
                Evidence = DecisionEvidenceClass.HardFailure, Service = ServiceKind.ChatGPT });
        Equal(AutomaticDecisionState.ConfirmingFailure, transition.Transaction.State,
            "hard failure bypasses stabilization");

        var oneRecent = new[] {
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-1), From = "a", To = "b" }
        };
        Equal(true, SwitchBudgetPolicy.CanSwitch(oneRecent, now).Allowed,
            "second switch inside ten minutes remains allowed");
        var twoRecent = new[] {
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-9), From = "a", To = "b" },
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-1), From = "b", To = "c" }
        };
        Equal(false, SwitchBudgetPolicy.CanSwitch(twoRecent, now).Allowed,
            "third switch inside ten minutes enters stabilization");
        var fourRecent = new[] {
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-29), From = "a", To = "b" },
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-20), From = "b", To = "c" },
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-11), From = "c", To = "d" },
            new AutomaticSwitchRecord { Utc = now.AddMinutes(-1), From = "d", To = "e" }
        };
        Equal(false, SwitchBudgetPolicy.CanSwitch(fourRecent, now).Allowed,
            "fifth switch inside thirty minutes enters stabilization");
        Equal(true, SwitchBudgetPolicy.CanSwitch(twoRecent, now.AddMinutes(2)).Allowed,
            "budget automatically releases when the rolling slot expires");
    }

    private static void RunEligibleFastFailoverOrchestration()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-fast-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] alternatives = Enumerable.Range(1, 10)
                .Select(x => "node-" + x.ToString("D2")).ToArray();
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current",
                new[] { "current" }.Concat(alternatives.Reverse()), delays);
            var probe = new OrchestratedServiceProbe(mihomo, true);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo, probe,
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc) },
                new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.RunOnce(false, UserPreferences.Defaults());

            Equal(10, mihomo.DelayNodes(2500).Count,
                "fast failover measures every alternative leaf with the 2500 ms delay budget");
            Equal(10, mihomo.DelayNodes(2500).Distinct(StringComparer.Ordinal).Count(),
                "fast failover does not repeat the all-node delay round");
            Equal(10, mihomo.AllDelayNodes().Count,
                "successful fast failover performs no second delay round with another timeout");
            Equal(10, mihomo.MaximumConcurrentDelayCalls,
                "all alternative Mihomo delay checks run in the same concurrent round");
            Equal("node-01,node-02,node-03",
                String.Join(",", mihomo.ScanNodes(TimeSpan.FromSeconds(2))),
                "quick scans follow current live delay order and stop at three eligible candidates");
            Equal("current,node-01,node-02",
                String.Join(",", mihomo.ScanNodes(TimeSpan.FromSeconds(5))),
                "winner full verification failure advances to the second ranked candidate");
            Equal("node-02", mihomo.GetSelected("shared"),
                "second ranked candidate is switched only after its full verification passes");
            string log = File.ReadAllText(Path.Combine(root, "logs", "monitor.log"));
            Equal(true, log.Contains("fast selection delay_ms=") && log.Contains("checked=3 eligible=3"),
                "fast selection orchestration records checked and eligible counts");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void RunEightCandidateFastFailoverBound()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-fast-bound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string[] alternatives = Enumerable.Range(1, 10)
                .Select(x => "node-" + x.ToString("D2")).ToArray();
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current",
                new[] { "current" }.Concat(alternatives.Reverse()), delays);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo,
                new OrchestratedServiceProbe(mihomo, false),
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc) },
                new OrchestratedExitIdentityProbe(mihomo, true), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.RunOnce(false, UserPreferences.Defaults());

            Equal(10, mihomo.DelayNodes(2500).Distinct(StringComparer.Ordinal).Count(),
                "unsupported exits still receive one concurrent all-node delay round");
            Equal("node-01,node-02,node-03,node-04,node-05,node-06,node-07,node-08",
                String.Join(",", mihomo.ScanNodes(TimeSpan.FromSeconds(2))),
                "fast failover checks at most eight low-delay candidates when none is region eligible");
            Equal("current", mihomo.GetSelected("shared"),
                "zero eligible quick scans never switch the shared group");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void RunActiveCircuitAutomaticFailover()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-active-circuit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc);
            var persisted = new ExperienceData();
            ServiceIncidentPolicy.Open(persisted.ServiceIncidents, ServiceKind.ChatGPT,
                ProbeFailureKind.Service, now.AddMinutes(-1), TimeSpan.FromMinutes(10));
            ServiceIncidentPolicy.Open(persisted.ServiceIncidents, ServiceKind.Discord,
                ProbeFailureKind.Service, now.AddMinutes(-1), TimeSpan.FromMinutes(10));
            new ExperienceStore(Path.Combine(root, "state", "experience.json")).Save(persisted, now);

            string[] alternatives = Enumerable.Range(1, 4)
                .Select(x => "node-" + x.ToString("D2")).ToArray();
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current",
                new[] { "current" }.Concat(alternatives.Reverse()), delays);
            var preferences = UserPreferences.Defaults();
            preferences.RequiredServices.Add(ServiceKind.Discord);
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo,
                new OrchestratedServiceProbe(mihomo, false),
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = now },
                new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            MonitorSnapshot snapshot = worker.Run(preferences);

            Equal(String.Join(",", preferences.RequiredServices.OrderBy(x => x)),
                String.Join(",", mihomo.ScanServices("current", 1).Distinct().OrderBy(x => x)),
                "automatic cycle rechecks every required service on the current node despite an active circuit");
            Equal("node-01", mihomo.GetSelected("shared"),
                "automatic cycle can fast-switch after the circuited service still fails on current and passes on a candidate");
            Equal(true, mihomo.DelayNodes(2500).Count > 0,
                "active-circuit automatic recovery reaches the live all-node delay round");
            Equal(String.Join(",", preferences.RequiredServices.OrderBy(x => x)),
                String.Join(",", snapshot.Services.Select(x => x.Service).Distinct().OrderBy(x => x)),
                "completed snapshot shows every required service actually probed on the selected node");
            Equal(false, snapshot.Services.Any(x => x.Evidence == ProbeFailureKind.Unverified),
                "completed snapshot does not replace actual required-service results with circuit placeholders");
            Equal(TimeSpan.FromSeconds(30), snapshot.NextCheckUtc - snapshot.CheckedUtc,
                "successful automatic failover keeps the thirty-second observation interval");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void RunConsensusCircuitDoesNotAttributeNodeFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-consensus-circuit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = new DateTime(2026, 9, 19, 11, 0, 0, DateTimeKind.Utc);
            string[] alternatives = { "node-01", "node-02" };
            var delays = alternatives.Select((node, index) => new { node, delay = (index + 1) * 10 })
                .ToDictionary(x => x.node, x => x.delay, StringComparer.Ordinal);
            var mihomo = new OrchestratedMihomo("current",
                new[] { "current" }.Concat(alternatives), delays);
            var failures = new Dictionary<string, ServiceKind>(StringComparer.Ordinal) {
                { "current", ServiceKind.ChatGPT }, { "node-01", ServiceKind.ChatGPT },
                { "node-02", ServiceKind.ChatGPT }
            };
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo,
                new OrchestratedFailureProbe(mihomo, failures),
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = now }, new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            MonitorSnapshot snapshot = worker.Run(UserPreferences.Defaults());

            Equal("current", mihomo.GetSelected("shared"),
                "three-node service consensus keeps the current node instead of switching");
            Equal(ProbeFailureKind.Service,
                snapshot.Services.First(x => x.Service == ServiceKind.ChatGPT).Evidence,
                "service consensus snapshot retains the real current-cycle failure evidence");
            HealthState history = new StateStore(FastWorkerConfiguration(root).StatePath).Load();
            Equal(true, history.Records.ContainsKey("current"),
                "unaffected services still contribute current-node health history");
            Equal(CandidateHealth.Compatible, history.Records["current"].Health,
                "shared failure is excluded from the current-node health result");
            QualitySample currentQuality = new QualityStateStore(
                FastWorkerConfiguration(root).QualityStatePath).Load().Last(x => x.Name == "current");
            Equal(100.0, currentQuality.ResponseMedianMs,
                "shared failure latency is excluded from node quality history");
            NodeExperience currentExperience = new ExperienceStore(
                Path.Combine(root, "state", "experience.json")).Load().Nodes.Last(x => x.Node == "current");
            Equal(true, currentExperience.LastPassed,
                "shared failure does not lower current-node experience history");
            Equal(100.0, currentExperience.ResponseMs,
                "experience response uses only attributable services");
            string log = File.ReadAllText(Path.Combine(root, "logs", "monitor.log"));
            Equal(true, log.Contains("service incident consensus service=ChatGPT fingerprints=3 " +
                "countries=1 asns=2 passed=True reason=none"),
                "incident trace contains only safe aggregate diversity evidence");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static void RunCircuitRollbackRechecksAllRequiredServices()
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-circuit-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTime now = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
            var preferences = UserPreferences.Defaults();
            string[] choices = { "node-01", "current" };
            string servicesKey = String.Join(",", preferences.RequiredServices.Distinct().OrderBy(x => x));
            string scope = WorkerScope(choices, servicesKey);
            var persisted = new ExperienceData {
                ActiveScope = scope,
                ActiveServicesKey = servicesKey,
                ActiveCandidateNames = choices.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                LastNode = "node-01",
                Assurance = new ConnectionAssurance {
                    Scope = scope, Previous = "current", Target = "node-01", FailedChecks = 1,
                    StartedUtc = now.AddMinutes(-1), Standbys = new List<StandbyNode>()
                }
            };
            ServiceIncidentPolicy.Open(persisted.ServiceIncidents, ServiceKind.ChatGPT,
                ProbeFailureKind.Service, now.AddMinutes(-1), TimeSpan.FromMinutes(10));
            new ExperienceStore(Path.Combine(root, "state", "experience.json")).Save(persisted, now);

            var mihomo = new OrchestratedMihomo("node-01", choices,
                new Dictionary<string, int>(StringComparer.Ordinal) { { "current", 10 } });
            var failures = new Dictionary<string, ServiceKind>(StringComparer.Ordinal) {
                { "node-01", ServiceKind.GitHub }, { "current", ServiceKind.ChatGPT }
            };
            var worker = new MonitorWorker(FastWorkerConfiguration(root), mihomo,
                new OrchestratedFailureProbe(mihomo, failures),
                new BoundedLogger(Path.Combine(root, "logs", "monitor.log"), 1024 * 1024),
                new FakeClock { UtcNow = now }, new OrchestratedExitIdentityProbe(mihomo, false), null,
                () => new RuntimeSnapshot(true, "", "verge-mihomo", false));

            worker.Run(preferences);

            Equal("node-01", mihomo.GetSelected("shared"),
                "observation rollback never returns to an old node that still fails a circuited required service");
            Equal(true, mihomo.ScanServices("current", 2).Contains(ServiceKind.ChatGPT),
                "old-node rollback verification probes all required services despite an active circuit");
        }
        finally
        {
            DeleteDirectoryEventually(root);
        }
    }

    private static string WorkerScope(IEnumerable<string> candidates, string servicesKey)
    {
        byte[] source = Encoding.UTF8.GetBytes(String.Join("\n", candidates.Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)));
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return Convert.ToBase64String(hash.ComputeHash(source)) + "|" + servicesKey;
    }

    private static MonitorConfiguration FastWorkerConfiguration(string root)
    {
        return new MonitorConfiguration {
            RootPath = root,
            StatePath = Path.Combine(root, "state", "health.state"),
            QualityStatePath = Path.Combine(root, "state", "quality.state"),
            SharedGroup = "shared",
            GeneralGroup = "general",
            ProbeGroup = "probe"
        };
    }

    private sealed class OrchestratedScanEvent
    {
        private readonly object gate = new object();
        private TimeSpan timeout;
        private readonly List<ServiceKind> services = new List<ServiceKind>();
        public OrchestratedScanEvent(string node, int visit)
        {
            Node = node;
            Visit = visit;
        }
        public string Node { get; private set; }
        public int Visit { get; private set; }
        public TimeSpan Timeout { get { lock (gate) return timeout; } }
        public void ObserveTimeout(TimeSpan value) { lock (gate) timeout = value; }
        public void ObserveService(ServiceKind service) { lock (gate) services.Add(service); }
        public ServiceKind[] Services() { lock (gate) return services.ToArray(); }
    }

    private sealed class OrchestratedMihomo : IMihomoClient
    {
        private readonly object gate = new object();
        private readonly string[] choices;
        private readonly Dictionary<string, int> delays;
        private readonly Dictionary<string, int> scanVisits = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<OrchestratedScanEvent> scans = new List<OrchestratedScanEvent>();
        private readonly List<KeyValuePair<string, int>> delayCalls = new List<KeyValuePair<string, int>>();
        private readonly CountdownEvent delayEntries;
        private string shared;
        private string general;
        private OrchestratedScanEvent activeScan;
        private int activeDelayCalls;
        private int maximumConcurrentDelayCalls;

        public OrchestratedMihomo(string selected, IEnumerable<string> choices,
            Dictionary<string, int> delays)
        {
            shared = selected;
            general = selected;
            this.choices = choices.ToArray();
            this.delays = delays;
            delayEntries = new CountdownEvent(delays.Count);
        }

        public int MaximumConcurrentDelayCalls
        {
            get { lock (gate) return maximumConcurrentDelayCalls; }
        }

        public string[] GetChoices(string groupName) { return choices.ToArray(); }

        public string GetSelected(string groupName)
        {
            lock (gate)
            {
                if (groupName == "shared") return shared;
                if (groupName == "general") return general;
                return activeScan == null ? "" : activeScan.Node;
            }
        }

        public void Select(string groupName, string proxyName)
        {
            lock (gate)
            {
                if (groupName == "shared") { shared = proxyName; return; }
                if (groupName == "general") { general = proxyName; return; }
                int visits;
                scanVisits.TryGetValue(proxyName, out visits);
                scanVisits[proxyName] = visits + 1;
                activeScan = new OrchestratedScanEvent(proxyName, visits + 1);
                scans.Add(activeScan);
            }
        }

        public int GetDelay(string proxyName, string url, int timeoutMilliseconds)
        {
            bool concurrentRound = timeoutMilliseconds == 2500;
            if (concurrentRound)
            {
                lock (gate)
                {
                    delayCalls.Add(new KeyValuePair<string, int>(proxyName, timeoutMilliseconds));
                    activeDelayCalls++;
                    maximumConcurrentDelayCalls = Math.Max(maximumConcurrentDelayCalls, activeDelayCalls);
                }
                delayEntries.Signal();
                delayEntries.Wait(TimeSpan.FromSeconds(3));
                lock (gate) activeDelayCalls--;
            }
            else lock (gate) delayCalls.Add(new KeyValuePair<string, int>(proxyName, timeoutMilliseconds));
            int delay;
            return delays.TryGetValue(proxyName, out delay) ? delay : Int32.MaxValue;
        }

        public bool IsRuntimeIpv6Enabled() { return false; }
        public bool IsAvailable() { return true; }

        public OrchestratedScanEvent ActiveScan()
        {
            lock (gate) return activeScan;
        }

        public List<string> DelayNodes(int timeoutMilliseconds)
        {
            lock (gate) return delayCalls.Where(x => x.Value == timeoutMilliseconds).Select(x => x.Key).ToList();
        }

        public List<string> AllDelayNodes()
        {
            lock (gate) return delayCalls.Select(x => x.Key).ToList();
        }

        public List<string> ScanNodes(TimeSpan timeout)
        {
            lock (gate) return scans.Where(x => x.Timeout == timeout).Select(x => x.Node).ToList();
        }

        public ServiceKind[] ScanServices(string node, int visit)
        {
            lock (gate)
            {
                OrchestratedScanEvent scan = scans.FirstOrDefault(x => x.Node == node && x.Visit == visit);
                return scan == null ? new ServiceKind[0] : scan.Services();
            }
        }
    }

    private sealed class OrchestratedServiceProbe : IServiceProbe
    {
        private readonly OrchestratedMihomo mihomo;
        private readonly bool failFirstFullWinner;
        public OrchestratedServiceProbe(OrchestratedMihomo mihomo, bool failFirstFullWinner)
        {
            this.mihomo = mihomo;
            this.failFirstFullWinner = failFirstFullWinner;
        }

        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        {
            OrchestratedScanEvent scan = mihomo.ActiveScan();
            scan.ObserveTimeout(timeout);
            scan.ObserveService(service);
            if (scan.Node == "current" && service == ServiceKind.ChatGPT)
                return ProbeResult.ServiceFailure("current failed", 900);
            if (failFirstFullWinner && scan.Node == "node-01" && scan.Visit == 2 &&
                service == ServiceKind.GitHub)
                return ProbeResult.ServiceFailure("winner full verification failed", 100);
            int number;
            if (!Int32.TryParse(scan.Node.Replace("node-", ""), out number)) number = 9;
            return ProbeResult.Success(number * 100);
        }
    }

    private sealed class SevereLatencyProbe : IServiceProbe
    {
        private readonly OrchestratedMihomo mihomo;
        private readonly long retryMilliseconds;
        private readonly long alternativeMilliseconds;
        private int currentChatGptCalls;

        public SevereLatencyProbe(OrchestratedMihomo mihomo, long retryMilliseconds)
            : this(mihomo, retryMilliseconds, 300)
        {
        }

        public SevereLatencyProbe(OrchestratedMihomo mihomo, long retryMilliseconds,
            long alternativeMilliseconds)
        {
            this.mihomo = mihomo;
            this.retryMilliseconds = retryMilliseconds;
            this.alternativeMilliseconds = alternativeMilliseconds;
        }

        public int CurrentChatGptCalls { get { return currentChatGptCalls; } }

        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        {
            OrchestratedScanEvent scan = mihomo.ActiveScan();
            scan.ObserveTimeout(timeout);
            scan.ObserveService(service);
            if (scan.Node == "current" && service == ServiceKind.ChatGPT)
            {
                int call = Interlocked.Increment(ref currentChatGptCalls);
                return ProbeResult.Success(call == 1 ? 2100 : retryMilliseconds);
            }
            return ProbeResult.Success(alternativeMilliseconds);
        }
    }

    private sealed class OrchestratedFailureProbe : IServiceProbe
    {
        private readonly OrchestratedMihomo mihomo;
        private readonly IDictionary<string, ServiceKind> failures;
        public OrchestratedFailureProbe(OrchestratedMihomo mihomo,
            IDictionary<string, ServiceKind> failures)
        {
            this.mihomo = mihomo;
            this.failures = failures;
        }

        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        {
            OrchestratedScanEvent scan = mihomo.ActiveScan();
            scan.ObserveTimeout(timeout);
            scan.ObserveService(service);
            ServiceKind failed;
            if (failures.TryGetValue(scan.Node, out failed) && failed == service)
                return ProbeResult.ServiceFailure("scripted service failure", 900);
            return ProbeResult.Success(100);
        }
    }

    private sealed class OpportunityServiceProbe : IServiceProbe
    {
        private readonly OrchestratedMihomo mihomo;
        private readonly long currentMilliseconds;
        private readonly long firstAlternativeMilliseconds;
        public OpportunityServiceProbe(OrchestratedMihomo mihomo)
            : this(mihomo, 1000, 150) { }
        public OpportunityServiceProbe(OrchestratedMihomo mihomo, long currentMilliseconds,
            long firstAlternativeMilliseconds)
        {
            this.mihomo = mihomo;
            this.currentMilliseconds = currentMilliseconds;
            this.firstAlternativeMilliseconds = firstAlternativeMilliseconds;
        }
        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        {
            OrchestratedScanEvent scan = mihomo.ActiveScan();
            scan.ObserveTimeout(timeout);
            scan.ObserveService(service);
            if (scan.Node == "current") return ProbeResult.Success(currentMilliseconds);
            int number;
            if (!Int32.TryParse(scan.Node.Replace("node-", ""), out number)) number = 9;
            long elapsed = firstAlternativeMilliseconds + (number - 1) * 50;
            return service == ServiceKind.ChatGPT || service == ServiceKind.Gemini
                ? ProbeResult.Partial("entry reachable", elapsed)
                : ProbeResult.Success(elapsed);
        }
    }

    private sealed class OrchestratedExitIdentityProbe : IExitIdentityProbe
    {
        private readonly OrchestratedMihomo mihomo;
        private readonly bool unsupportedAlternatives;
        public OrchestratedExitIdentityProbe(OrchestratedMihomo mihomo, bool unsupportedAlternatives)
        {
            this.mihomo = mihomo;
            this.unsupportedAlternatives = unsupportedAlternatives;
        }

        public ExitIdentity Probe(TimeSpan timeout)
        {
            OrchestratedScanEvent scan = mihomo.ActiveScan();
            int number;
            if (!Int32.TryParse(scan.Node.Replace("node-", ""), out number)) number = 999;
            string country = unsupportedAlternatives && scan.Node != "current" ? "HK" : "JP";
            long asn = scan.Node == "current" ? 64530 : 64531;
            return new ExitIdentity(((long)number + 10000).ToString("X64"), country, "ok", asn,
                new DateTime(2026, 9, 21, 4, 0, 0, DateTimeKind.Utc));
        }
    }

    private sealed class FakeProbe : IServiceProbe
    {
        public readonly List<ServiceKind> Calls = new List<ServiceKind>();
        public readonly List<TimeSpan> Timeouts = new List<TimeSpan>();
        public readonly Dictionary<ServiceKind, ProbeResult> Results = new Dictionary<ServiceKind, ProbeResult>();
        public ProbeResult DefaultResult = ProbeResult.Success();
        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        {
            lock (Calls)
            {
                Calls.Add(service);
                Timeouts.Add(timeout);
            }
            ProbeResult result;
            return Results.TryGetValue(service, out result) ? result : DefaultResult;
        }
    }

    private sealed class ConcurrentEntryProbe : IServiceProbe
    {
        private readonly CountdownEvent entered;
        public ConcurrentEntryProbe(int count) { entered = new CountdownEvent(count); }
        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        {
            entered.Signal();
            return entered.Wait(TimeSpan.FromMilliseconds(500))
                ? ProbeResult.Success(100)
                : ProbeResult.ServiceFailure("probes were serialized");
        }
    }

    private sealed class CancellationAwareStream : Stream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<int>();
            cancellationToken.Register(() => completion.TrySetCanceled());
            return completion.Task;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    private sealed class SelectorMihomo : IMihomoClient
    {
        private readonly Dictionary<string, string> selected = new Dictionary<string, string> {
            { "probe", "probe-node" }, { "direct", "DIRECT" }
        };
        private readonly string[] generalChoices;
        public int SelectCalls;
        public SelectorMihomo(string shared, string general, string[] choices)
        {
            selected["shared"] = shared;
            selected["general"] = general;
            generalChoices = choices;
        }
        public void SetShared(string value) { selected["shared"] = value; }
        public void SetGeneral(string value) { selected["general"] = value; }
        public string[] GetChoices(string groupName) { return groupName == "general" ? generalChoices : new string[0]; }
        public string GetSelected(string groupName) { return selected[groupName]; }
        public void Select(string groupName, string proxyName) { selected[groupName] = proxyName; SelectCalls++; }
        public int GetDelay(string proxyName, string url, int timeoutMilliseconds) { return 50; }
        public bool IsRuntimeIpv6Enabled() { return false; }
        public bool IsAvailable() { return true; }
    }

    private sealed class FakeMihomo : IMihomoClient
    {
        public string LastSelected;
        public string[] GetChoices(string groupName) { return new string[0]; }
        public string GetSelected(string groupName) { return LastSelected; }
        public void Select(string groupName, string proxyName) { LastSelected = proxyName; }
        public int GetDelay(string proxyName, string url, int timeoutMilliseconds) { return 50; }
        public bool IsRuntimeIpv6Enabled() { return false; }
        public bool IsAvailable() { return true; }
    }

    private sealed class VerificationMihomo : IMihomoClient
    {
        private readonly string[] choices;
        private string sharedSelected;
        public VerificationMihomo(string selected, string[] choices)
        { sharedSelected = selected; this.choices = choices; }
        public string[] GetChoices(string groupName) { return groupName == "shared" ? choices : choices; }
        public string GetSelected(string groupName) { return groupName == "shared" ? sharedSelected : ""; }
        public void Select(string groupName, string proxyName) { if (groupName == "shared") sharedSelected = proxyName; }
        public int GetDelay(string proxyName, string url, int timeoutMilliseconds) { return 50; }
        public bool IsRuntimeIpv6Enabled() { return false; }
        public bool IsAvailable() { return true; }
    }

    private static void QualityScoringAndState()
    {
        DateTime now = new DateTime(2026, 9, 6, 1, 0, 0, DateTimeKind.Utc);
        var stable = new QualitySample("稳定 1x", now, true, 120, 8, 900000, 1.0);
        var jittery = new QualitySample("抖动 2x", now, true, 120, 80, 300000, 2.0);
        var cohort = new[] { stable, jittery };
        var stableHistory = new[] {
            new QualitySample(stable.Name, now.AddMinutes(-5), true, 130, 10, 0, 1.0),
            new QualitySample(stable.Name, now.AddMinutes(-20), true, 115, 9, 0, 1.0),
            new QualitySample(stable.Name, now.AddMinutes(-60), true, 150, 12, 0, 1.0)
        };
        QualityBreakdown good = QualityScorer.Score(stable, stableHistory, cohort, now);
        QualityBreakdown bad = QualityScorer.Score(jittery, new[] { jittery }, cohort, now);
        Equal(40.0, good.StabilityWeight, "stability weight");
        Equal(true, good.Score > bad.Score, "stable throughput and cost wins");
        Equal(true, good.Score >= 0 && good.Score <= 100 && good.Responsiveness >= 0 && good.Responsiveness <= 100, "quality components bounded");
        Equal(1.5, QualityScorer.ParseMultiplier("节点 | 1.5x").Value, "decimal multiplier parsed");
        Equal(2.0, QualityScorer.ParseMultiplier("节点 倍率 2").Value, "chinese multiplier parsed");
        var selectionCandidates = Enumerable.Range(0, 10).Select(i => new CandidateNode("n" + i, 1)).ToList();
        var delays = selectionCandidates.ToDictionary(x => x.Name, x => x.Name == "n9" ? 999 : Int32.Parse(x.Name.Substring(1)));
        var preselected = CandidatePreselector.Select(selectionCandidates, delays, "n9", 8);
        Equal(8, preselected.Count, "latency preselection cap");
        Equal(true, preselected.Any(x => x.Name == "n9"), "current node retained in preselection");
        Equal(false, RefreshPolicy.ShouldRunThroughput(false, CandidateHealth.Transient, now.AddHours(-1), now, TimeSpan.FromHours(6)), "transient failure does not repeat throughput");
        Equal(false, RefreshPolicy.ShouldRefreshCandidates(false, CandidateHealth.Unknown, false), "pending does not rescan entire pool each minute");
        Equal(true, RefreshPolicy.ShouldRefreshCandidates(false, CandidateHealth.Unknown, true), "scheduled pending rescan allowed");
        Equal(true, RefreshPolicy.ShouldRefreshCandidates(false, CandidateHealth.ServiceFailed, false), "definite failure triggers alternatives");
        Equal(true, RefreshPolicy.ShouldRunThroughput(false, CandidateHealth.Compatible, now.AddHours(-6), now, TimeSpan.FromHours(6)), "six hour throughput refresh");
        Equal(true, RefreshPolicy.ShouldRunThroughput(true, CandidateHealth.Compatible, now, now, TimeSpan.FromHours(6)), "subscription change refreshes throughput");

        string directory = Path.Combine(Path.GetTempPath(), "clash-quality-" + Guid.NewGuid().ToString("N"));
        string qualityPath = Path.Combine(directory, "quality.state");
        var qualityStore = new QualityStateStore(qualityPath);
        qualityStore.Save(stableHistory.Concat(new[] { stable, jittery }));
        Equal(5, qualityStore.Load().Count, "quality state roundtrip");
        File.WriteAllText(qualityPath, "broken", Encoding.UTF8);
        Equal(0, qualityStore.Load().Count, "quality corrupt recovery");
        Equal(true, Directory.GetFiles(directory, "quality.state.corrupt-*").Length == 1, "quality corrupt archived");
        var many = Enumerable.Range(0, 121).Select(i => new QualitySample("bounded", now.AddMinutes(i), true, 10, 1, 1, 1));
        var bounded = QualityStateStore.Bound(many);
        Equal(120, bounded.Count, "quality in-memory history bounded");
        Equal(now.AddMinutes(120), bounded.Max(x => x.CheckedUtc), "quality bound keeps newest sample");
    }

    private static void ThroughputAndTraffic()
    {
        var payload = new MemoryStream(new byte[2 * 1024 * 1024]);
        ThroughputResult measured = ThroughputProbe.Measure(payload, 1024 * 1024, 1000);
        Equal(1048576L, measured.BytesRead, "one MiB cap");
        Equal(1048576.0, measured.BytesPerSecond, "throughput calculation");

        var budget = new ThroughputBudget(5L * 1024 * 1024);
        for (int i = 0; i < 5; i++) Equal(true, budget.TryReserve(1024 * 1024), "throughput budget slot " + (i + 1));
        Equal(false, budget.TryReserve(1024 * 1024), "sixth throughput candidate skipped");
        Equal(5L * 1048576L, budget.MaximumBytes, "round budget");

        var clock = new FakeClock { UtcNow = new DateTime(2026, 9, 6, 1, 0, 0, DateTimeKind.Utc) };
        var guard = new TrafficGuard(clock, 3.0 * 1024 * 1024, TimeSpan.FromMinutes(5));
        Equal(false, guard.MayProbe(3.1 * 1024 * 1024), "foreground traffic postpones");
        clock.UtcNow = clock.UtcNow.AddMinutes(4);
        Equal(false, guard.MayProbe(0), "traffic cooldown holds");
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Equal(true, guard.MayProbe(0), "traffic cooldown recovers");
    }

    private static void StabilityAndState()
    {
        var clock = new FakeClock { UtcNow = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc) };
        var controller = new FailoverController(clock, TimeSpan.FromMinutes(10));
        Equal(true, SwitchModePolicy.AllowsAutomaticSwitch(false, false), "fault failover remains enabled in conservative mode");
        Equal(false, SwitchModePolicy.AllowsAutomaticSwitch(true, false), "healthy current holds in conservative mode");
        Equal(true, SwitchModePolicy.AllowsAutomaticSwitch(true, true), "advanced mode may optimize healthy current");
        Equal(false, SwitchModePolicy.ShouldEvaluateQuality(true, false),
            "conservative mode skips quality comparison for usable current");
        Equal("current usable; conservative mode holds", SwitchModePolicy.ConservativeHoldReason,
            "conservative mode has stable decision reason");
        Equal(500.0, QualityPolicy.PreferredResponseMilliseconds, "preferred candidate response threshold");
        Equal("优秀", QualityPolicy.LatencyBand(300), "excellent latency band boundary");
        Equal("良好", QualityPolicy.LatencyBand(500), "good latency band boundary");
        Equal("可用但偏慢", QualityPolicy.LatencyBand(800), "usable but slow latency band boundary");
        Equal("较慢", QualityPolicy.LatencyBand(801), "slow latency band starts above 800");
        Equal(false, QualityPolicy.CurrentNeedsOptimization(new[] { 900.0, 1000.0 }), "current latency needs three samples");
        Equal(false, QualityPolicy.CurrentNeedsOptimization(new[] { 700.0, 800.0, 900.0 }), "current median at 800 holds");
        Equal(true, QualityPolicy.CurrentNeedsOptimization(new[] { 700.0, 801.0, 900.0 }), "current median above 800 may optimize");
        Equal(true, QualityPolicy.CandidateLatencyIsPreferred(new[] { 300.0, 400.0, 500.0, 500.0, 500.0 }), "candidate median 500 and jitter pass");
        Equal(false, QualityPolicy.CandidateLatencyIsPreferred(new[] { 300.0, 400.0, 501.0, 501.0, 501.0 }), "candidate median above 500 is not preferred");
        Equal(false, QualityPolicy.CandidateLatencyIsPreferred(new[] { 300.0, 400.0, 500.0, 600.0 }), "candidate latency needs five samples");
        Equal(false, QualityPolicy.CandidateLatencyIsPreferred(new[] { 650.0, 700.0, 800.0, 900.0, 1501.0 }), "candidate maximum above 1500 fails");
        Equal(false, QualityPolicy.CandidateLatencyIsPreferred(new[] { 100.0, 300.0, 500.0, 700.0, 800.0 }), "candidate jitter above 150 fails");
        var responsiveServices = new CandidateScanResult("target", CandidateHealth.Compatible, null, "ok", 1400, 2,
            new Dictionary<ServiceKind, ProbeResult> { { ServiceKind.Google, ProbeResult.Success(400) }, { ServiceKind.GitHub, ProbeResult.Success(1500) } });
        var slowService = new CandidateScanResult("target", CandidateHealth.Compatible, null, "ok", 1401, 2,
            new Dictionary<ServiceKind, ProbeResult> { { ServiceKind.Google, ProbeResult.Success(400) }, { ServiceKind.GitHub, ProbeResult.Success(1501) } });
        Equal(true, QualityPolicy.ServicesWithinLimit(responsiveServices), "service response at 1500 passes");
        Equal(false, QualityPolicy.ServicesWithinLimit(slowService), "single slow service rejects quality candidate");
        double[] slowCurrent = { 700, 801, 900 };
        double[] preferredTarget = { 300, 400, 500, 500, 500 };
        Equal(false, controller.DecideQuality(70, 82, false, true, true, slowCurrent, preferredTarget, responsiveServices).ShouldSwitch, "under 20 percent holds");
        Equal(true, controller.DecideQuality(70, 85, false, true, true, slowCurrent, preferredTarget, responsiveServices).ShouldSwitch, "over 20 percent switches to preferred latency");
        Equal(false, controller.DecideQuality(70, 90, false, false, true, slowCurrent, preferredTarget, responsiveServices).ShouldSwitch, "fresh verification required");
        Equal(false, controller.DecideQuality(70, 90, false, true, false, slowCurrent, preferredTarget, responsiveServices).ShouldSwitch, "proven stable history required");
        Equal(false, controller.DecideQuality(70, 90, false, true, true, new[] { 700.0, 800.0, 900.0 }, preferredTarget, responsiveServices).ShouldSwitch, "current latency not persistently slow holds");
        Equal(false, controller.DecideQuality(70, 90, false, true, true, slowCurrent, new[] { 650.0, 700.0, 800.0, 900.0, 1501.0 }, responsiveServices).ShouldSwitch, "candidate history outside preferred latency cannot switch");
        Equal(false, controller.DecideQuality(70, 90, false, true, true, slowCurrent, preferredTarget, slowService).ShouldSwitch, "single slow service cannot quality switch");
        var reachableTarget = new CandidateScanResult("target", CandidateHealth.BasicCompatible, null, "challenge", 100, 1,
            new Dictionary<ServiceKind, ProbeResult> { { ServiceKind.ChatGPT, ProbeResult.Partial("challenge", 100) } });
        Equal(false, controller.DecideQuality(70, 90, false, true, true, slowCurrent, preferredTarget, reachableTarget).ShouldSwitch,
            "reachable-only target cannot quality switch");
        Equal(true, controller.DecideQuality(70, 90, true, true, false, new double[0], new double[0], slowService).ShouldSwitch, "disconnect bypasses quality history and latency gates");
        var healthy = new[] { new NodeHealthRecord("next", CandidateHealth.Compatible, clock.UtcNow, clock.UtcNow, false) };
        Equal(false, controller.Decide(false, false, "current", healthy).ShouldSwitch, "first failure stays");
        Equal(true, controller.Decide(false, false, "current", healthy).ShouldSwitch, "second failure switches");
        controller.RecordSwitch();
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Equal(false, controller.Decide(false, false, "current", healthy).ShouldSwitch, "minimum hold");
        Equal(true, controller.Decide(false, true, "current", healthy).ShouldSwitch, "disconnect bypasses hold");
        controller.Decide(true, false, "current", healthy);
        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        Equal(false, controller.Decide(false, false, "current", healthy).ShouldSwitch, "success resets failure count");
        Equal(false, controller.Decide(false, false, "current", new[] { new NodeHealthRecord("unknown", CandidateHealth.Unknown, clock.UtcNow, clock.UtcNow, false) }).ShouldSwitch, "unknown never selected");

        var basicController = new FailoverController(clock, TimeSpan.FromMinutes(10));
        var basic = new[] { new NodeHealthRecord("basic", CandidateHealth.BasicCompatible, clock.UtcNow, clock.UtcNow, false) };
        Equal(false, basicController.Decide(false, false, "current", basic).ShouldSwitch, "first basic-compatible failure stays");
        Equal(false, basicController.Decide(false, false, "current", basic).ShouldSwitch, "basic-compatible replacement remains ineligible");

        Equal(clock.UtcNow.AddMinutes(5), HealthPolicy.CooldownUntil(CandidateHealth.Transient, clock.UtcNow), "transient cooldown");
        Equal(clock.UtcNow.AddMinutes(30), HealthPolicy.CooldownUntil(CandidateHealth.ServiceFailed, clock.UtcNow), "service cooldown");

        string statePath = Path.Combine(Path.GetTempPath(), "clash-monitor-state-" + Guid.NewGuid().ToString("N"), "health.state");
        var state = new HealthState("old");
        state.Records["节点\t一"] = new NodeHealthRecord("节点\t一", CandidateHealth.Compatible, clock.UtcNow, clock.UtcNow, false);
        state.Records["香港"] = new NodeHealthRecord("香港", CandidateHealth.RegionBlocked, clock.UtcNow, clock.UtcNow, true);
        state.RememberPreferred("节点\t一", CandidateHealth.Compatible, clock.UtcNow);
        var store = new StateStore(statePath);
        store.Save(state, state.Records.Keys);
        var loaded = store.Load();
        Equal(2, loaded.Records.Count, "state roundtrip count");
        Equal("节点\t一", loaded.PreferredNode, "preferred node roundtrip");
        Equal(clock.UtcNow, loaded.PreferredNodeVerifiedUtc, "preferred time roundtrip");
        loaded.RememberPreferred("失败节点", CandidateHealth.Transient, clock.UtcNow.AddMinutes(1));
        Equal("节点\t一", loaded.PreferredNode, "failure does not replace preferred node");
        loaded.ApplySubscriptionFingerprint("new");
        Equal(CandidateHealth.Unknown, loaded.Records["节点\t一"].Health, "fingerprint invalidates probes");
        Equal(CandidateHealth.RegionBlocked, loaded.Records["香港"].Health, "fingerprint preserves local exclusion");

        var recovery = new HealthState("same");
        recovery.Records["稳定节点"] = new NodeHealthRecord("稳定节点", CandidateHealth.Compatible,
            clock.UtcNow, clock.UtcNow, false);
        recovery.RememberPreferred("稳定节点", CandidateHealth.Compatible, clock.UtcNow);
        var recoveryCandidates = new[] { new CandidateNode("稳定节点", 1), new CandidateNode("其他节点", 1) };
        Equal("稳定节点", ReloadRecovery.ChooseTarget(true, recovery, recoveryCandidates, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "recent strict node restored");
        recovery.ApplySubscriptionFingerprint("changed");
        Equal("稳定节点", ReloadRecovery.ChooseTarget(true, recovery, recoveryCandidates, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "recent preferred node invalidated by reload is selected for live recheck");
        recovery.Records["稳定节点"].Health = CandidateHealth.BasicCompatible;
        Equal<string>(null, ReloadRecovery.ChooseTarget(false, recovery, recoveryCandidates, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "manual selector change is not overridden");
        Equal<string>(null, ReloadRecovery.ChooseTarget(true, recovery, recoveryCandidates, clock.UtcNow.AddMinutes(31),
            TimeSpan.FromMinutes(30)), "stale preferred node not restored");
        Equal<string>(null, ReloadRecovery.ChooseTarget(true, recovery, new[] { new CandidateNode("其他节点", 1) }, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "removed preferred node not restored");
        recovery.Records["稳定节点"].Health = CandidateHealth.Transient;
        Equal<string>(null, ReloadRecovery.ChooseTarget(true, recovery, recoveryCandidates, clock.UtcNow,
            TimeSpan.FromMinutes(30)), "failed preferred node not restored");
    }

    private sealed class FakeExitIdentityProbe : IExitIdentityProbe
    {
        private readonly ExitIdentity identity;
        public FakeExitIdentityProbe(ExitIdentity identity) { this.identity = identity; }
        public ExitIdentity Probe(TimeSpan timeout) { return identity; }
    }

    private static void OpportunityCandidatePlanningBehavior()
    {
        DateTime now = new DateTime(2026, 9, 21, 11, 0, 0, DateTimeKind.Utc);
        string scope = "scope";
        CandidateNode[] candidates = new[] { "current" }
            .Concat(Enumerable.Range(1, 9).Select(x => "node-" + x.ToString("D2")))
            .Select(x => new CandidateNode(x, null)).ToArray();
        Dictionary<string, int> delays = Enumerable.Range(1, 9)
            .ToDictionary(x => "node-" + x.ToString("D2"), x => x * 10, StringComparer.Ordinal);
        var experience = new ExperienceData {
            Nodes = new List<NodeExperience> {
                new NodeExperience { Scope = scope, Node = "node-06", FirstUtc = now.AddHours(-1),
                    LastUtc = now.AddMinutes(-1), Samples = 6, Success = 1, LastPassed = true,
                    ResponseMs = 300, RecentResponseMilliseconds = new List<double> { 300, 310, 290 } },
                new NodeExperience { Scope = scope, Node = "node-07", FirstUtc = now.AddHours(-1),
                    LastUtc = now.AddMinutes(-2), Samples = 5, Success = 1, LastPassed = true,
                    ResponseMs = 350, RecentResponseMilliseconds = new List<double> { 350, 340, 360 } }
            }
        };

        OpportunityCandidatePlan plan = OpportunityCandidatePlanner.Create(
            candidates, delays, "current", experience, scope, now);

        Equal("node-01:LiveDelay,node-06:Historical,node-08:Exploration,node-02:LiveDelay," +
            "node-07:Historical,node-03:LiveDelay,node-04:LiveDelay,node-05:LiveDelay",
            String.Join(",", plan.Candidates.Select(x => x.Name + ":" + x.Source)),
            "opportunity plan interleaves five live two historical and one exploration candidate");
        Equal(8, plan.Candidates.Count, "opportunity plan remains bounded to eight candidates");
        Equal(5, plan.LiveDelayCount, "opportunity plan carries five live-delay candidates");
        Equal(2, plan.HistoricalCount, "opportunity plan carries two historical candidates");
        Equal(1, plan.ExplorationCount, "opportunity plan carries one under-tested candidate");
        Equal(0, plan.FillCount, "complete source quotas need no fill");
        Equal(false, plan.Candidates.Any(x => x.Name == "current"),
            "opportunity plan excludes the current node");

        experience.Nodes.Insert(0, new NodeExperience { Scope = scope, Node = "node-02",
            FirstUtc = now.AddHours(-1), LastUtc = now.AddMinutes(-1), Samples = 8,
            Success = 1, LastPassed = true, ResponseMs = 100,
            RecentResponseMilliseconds = new List<double> { 100, 100, 100 } });
        OpportunityCandidatePlan overlap = OpportunityCandidatePlanner.Create(
            candidates, delays, "current", experience, scope, now);
        Equal(true, overlap.DuplicateRemovalCount > 0,
            "historical overlap is recorded and never duplicates a live candidate");
        Equal(overlap.Candidates.Count, overlap.Candidates.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count(),
            "opportunity plan contains distinct nodes");

        OpportunityCandidatePlan fallback = OpportunityCandidatePlanner.Create(
            candidates.Take(8), delays, "current", new ExperienceData(), scope, now);
        Equal("node-01:LiveDelay,node-06:Exploration,node-02:LiveDelay,node-03:LiveDelay," +
            "node-04:LiveDelay,node-05:LiveDelay,node-07:Fill",
            String.Join(",", fallback.Candidates.Select(x => x.Name + ":" + x.Source)),
            "missing history safely falls back to exploration and live-delay fill");

        string[] recovery = StartupRecovery.RankFastCandidates(candidates, delays, "current", 8);
        Equal("node-01,node-02,node-03,node-04,node-05,node-06,node-07,node-08",
            String.Join(",", recovery),
            "failure recovery remains strict live-delay order");

        OpportunityCandidatePlan empty = OpportunityCandidatePlanner.Create(
            null, null, "current", null, scope, now);
        Equal(0, empty.Candidates.Count, "empty opportunity input produces an empty safe plan");

        var stale = new ExperienceData { Nodes = new List<NodeExperience> {
            new NodeExperience { Scope = scope, Node = "node-06", FirstUtc = now.AddDays(-9),
                LastUtc = now.AddDays(-8), Samples = 20, Success = 1, LastPassed = true,
                ResponseMs = 1, RecentResponseMilliseconds = new List<double> { 1, 1, 1 } },
            new NodeExperience { Scope = "other-scope", Node = "node-07", FirstUtc = now.AddHours(-1),
                LastUtc = now.AddMinutes(-1), Samples = 20, Success = 1, LastPassed = true,
                ResponseMs = 1, RecentResponseMilliseconds = new List<double> { 1, 1, 1 } }
        } };
        OpportunityCandidatePlan stalePlan = OpportunityCandidatePlanner.Create(
            candidates, delays, "current", stale, scope, now);
        Equal(0, stalePlan.HistoricalCount,
            "stale and foreign-scope history cannot enter opportunity history slots");
        Equal("node-07", stalePlan.Candidates.First(x => x.Source == OpportunityCandidateSource.Exploration).Name,
            "foreign-scope history is treated as unseen for deterministic exploration");

        Dictionary<string, int> tiedDelays = candidates.Where(x => x.Name != "current")
            .ToDictionary(x => x.Name, x => 100, StringComparer.Ordinal);
        OpportunityCandidatePlan tied = OpportunityCandidatePlanner.Create(
            candidates, tiedDelays, "current", new ExperienceData(), scope, now);
        Equal("node-01", tied.Candidates.First().Name,
            "ordinal node name resolves equal live-delay ordering deterministically");
        Equal(true, tied.Candidates.Count <= OpportunityCandidatePlanner.MaximumCandidates,
            "every opportunity plan respects the hard maximum");
    }

    private sealed class FakeExitAsnResolver : IExitAsnResolver
    {
        private readonly ExitAsnResolution resolution;
        public int Calls { get; private set; }
        public FakeExitAsnResolver(ExitAsnResolution resolution) { this.resolution = resolution; }
        public ExitAsnResolution Resolve(ExitIdentity expected, TimeSpan timeout)
        {
            Calls++;
            return resolution;
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; }
    }

    private static void RuntimeGuards()
    {
        var detector = new ConflictDetector();
        Equal(true, detector.Evaluate(new RuntimeSnapshot(false, "127.0.0.1:7897", "verge-mihomo", false)).Paused, "missing pipe pauses");
        Equal(true, detector.Evaluate(new RuntimeSnapshot(true, "127.0.0.1:7897", "other", false)).Paused, "wrong port owner pauses");
        Equal(true, detector.Evaluate(new RuntimeSnapshot(true, "127.0.0.1:8888", "verge-mihomo", false)).Paused, "wrong system proxy pauses");
        Equal(false, detector.Evaluate(new RuntimeSnapshot(true, "", "verge-mihomo", false)).Paused, "disabled system proxy allowed with mihomo routing");
        Equal(true, detector.Evaluate(new RuntimeSnapshot(true, "127.0.0.1:7897", "verge-mihomo", true)).Paused, "other vpn route pauses");
        Equal(false, detector.Evaluate(new RuntimeSnapshot(true, "127.0.0.1:7897", "verge-mihomo", false)).Paused, "normal runtime proceeds");

        var optional = OptionalServiceActivator.FromProcessNames(new[] { "Discord.exe", "Spotify", "EpicGamesLauncher.exe", "chrome.exe" });
        Equal(true, optional.Contains(ServiceKind.Discord), "discord process activates");
        Equal(true, optional.Contains(ServiceKind.Spotify), "spotify process activates");
        Equal(true, optional.Contains(ServiceKind.Epic), "epic process activates");
        Equal(0, OptionalServiceActivator.FromProcessNames(new[] { "chrome.exe" }).Count, "browser discord not inferred");

        string logPath = Path.Combine(Path.GetTempPath(), "clash-monitor-log-" + Guid.NewGuid().ToString("N"), "monitor.log");
        var logger = new BoundedLogger(logPath, 1024);
        for (int i = 0; i < 100; i++) logger.Write("state-" + i + " " + new string('x', 80));
        Equal(true, new FileInfo(logPath).Length <= 1024, "bounded log size");
    }

    private static void ProxyPathHealthBehavior()
    {
        Equal(false, ProxyPathHealthPolicy.ShouldCheck(false),
            "stable selector does not pay the path diagnostic cost every cycle");
        Equal(true, ProxyPathHealthPolicy.ShouldCheck(true),
            "changed selector is checked after alignment");
        Equal(false, ProxyPathHealth.Evaluate(true, true, true, true).Mismatch,
            "both proxy paths healthy allow normal decisions");
        Equal(true, ProxyPathHealth.Evaluate(false, false, true, true).Mismatch,
            "probe path failing while system path works pauses decisions");
        Equal(false, ProxyPathHealth.Evaluate(false, false, true, true).CanAutoSwitch,
            "path mismatch blocks automatic switching");
        Equal(true, ProxyPathHealth.Evaluate(true, true, false, false).Mismatch,
            "system path failing while probe path works pauses decisions");
        Equal(false, ProxyPathHealth.Evaluate(false, false, false, false).Mismatch,
            "both paths failing is an outage, not a path mismatch");
        Equal(true, ProxyPathHealth.Evaluate(false, false, false, false).CanAutoSwitch,
            "both paths failing preserves confirmed emergency failover");
        Equal(true, ProxyPathHealth.Evaluate(true, false, false, true).Mismatch,
            "different reachable sites across paths reveal a routing mismatch");
        Equal(true, ProxyPathHealth.Evaluate(true, true, true, false).Mismatch,
            "one failing site on the system path reveals a routing mismatch");
        Equal("Google：探测入口可用、系统代理入口失败；GitHub：探测入口失败、系统代理入口可用",
            ProxyPathHealth.Evaluate(true, false, false, true).MismatchDetail,
            "path mismatch identifies each affected site and route");
        Equal("", ProxyPathHealth.Evaluate(true, true, true, true).MismatchDetail,
            "matching routes do not report a mismatch detail");
    }

    private static void SelectorFollowerBehavior()
    {
        var nested = new SelectorMihomo("node-a", "old-node", new[] { "shared" });
        Equal(true, SelectorFollower.Synchronize(nested, "shared", "general"),
            "main selector binds to the shared group when the persistent group reference is available");
        Equal("shared", nested.GetSelected("general"), "general selector follows the shared group reference");
        nested.SetShared("node-b");
        Equal(false, SelectorFollower.Synchronize(nested, "shared", "general"),
            "nested shared reference follows changed nodes without another selection write");
        var mihomo = new SelectorMihomo("node-a", "subscription notice", new[] { "node-a", "node-b" });
        Equal(true, SelectorFollower.Synchronize(mihomo, "shared", "general"),
            "general selector follows verified shared node");
        Equal("node-a", mihomo.GetSelected("general"), "general selector uses shared node");
        Equal("probe-node", mihomo.GetSelected("probe"), "isolated probe selector is untouched");
        Equal("DIRECT", mihomo.GetSelected("direct"), "direct selector is untouched");
        Equal(1, mihomo.SelectCalls, "one selector update was made");
        Equal(false, SelectorFollower.Synchronize(mihomo, "shared", "general"),
            "matching selector does not repeat an update");
        Equal(1, mihomo.SelectCalls, "matching selector keeps the original update count");

        mihomo.SetShared("node-b");
        Equal(true, SelectorFollower.Synchronize(mihomo, "shared", "general"),
            "automatic shared-node change is followed");
        Equal("node-b", mihomo.GetSelected("general"), "general selector follows changed shared node");

        var unavailable = new SelectorMihomo("node-c", "subscription notice", new[] { "node-a" });
        Equal(false, SelectorFollower.Synchronize(unavailable, "shared", "general"),
            "node missing from general choices is not selected");
        Equal("subscription notice", unavailable.GetSelected("general"),
            "missing choice preserves the existing selector");

        mihomo.SetGeneral("subscription notice");
        Equal(false, SelectorFollower.SynchronizeVerified(mihomo, "shared", "general",
            new CandidateScanResult("node-b", CandidateHealth.Transient, null, "timeout")),
            "failed shared node is not copied into ordinary routing");
        Equal("subscription notice", mihomo.GetSelected("general"),
            "failed node leaves ordinary routing unchanged");
        Equal(true, SelectorFollower.SynchronizeVerified(mihomo, "shared", "general",
            new CandidateScanResult("node-b", CandidateHealth.BasicCompatible, null, "entry reachable")),
            "verified basic-compatible shared node can be followed");
    }

    private static void CommandLineBehavior()
    {
        var options = MonitorOptions.Parse(new[] { "--dry-run", "--once" });
        Equal(true, options.DryRun, "dry run parsed");
        Equal(true, options.Once, "once parsed");
        Equal(false, options.SelfTest, "self test absent");
        Equal(true, MonitorOptions.Parse(new[] { "--self-test" }).SelfTest, "self test parsed");
        string name = "ClashCompatibilityMonitor.Test." + Guid.NewGuid().ToString("N");
        using (var first = SingleInstanceLease.TryAcquire(name))
        using (var second = SingleInstanceLease.TryAcquire(name))
        {
            Equal(true, first != null, "first instance acquired");
            Equal(true, second == null, "second instance rejected");
        }
    }

    private static void InstanceActivationBehavior()
    {
        string id = "ClashCompatibilityMonitor.Test." + Guid.NewGuid().ToString("N");
        using (var activated = new ManualResetEventSlim(false))
        using (var first = InstanceActivation.TryOwn(id))
        {
            Equal(true, first.IsOwner, "first activation owns instance");
            first.Activated += delegate { activated.Set(); };
            first.StartListening();
            using (var second = InstanceActivation.TryOwn(id))
                Equal(false, second.IsOwner, "second activation signals owner");
            Equal(true, activated.Wait(1000), "existing instance activated");
        }
    }

    private static void UserPreferenceBehavior()
    {
        Equal(false, Enum.GetNames(typeof(ServiceKind)).Contains("JMComicWeb"), "jmcomic service removed");
        string root = Path.Combine(Path.GetTempPath(), "monitor-prefs-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "preferences.state");
        var store = new UserPreferenceStore(path);
        UserPreferences defaults = store.Load();
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.ChatGPT), "default includes ChatGPT");
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.Gemini), "default includes Gemini");
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.Google), "default includes Google");
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.GitHub), "default includes GitHub");
        Equal(true, defaults.RequiredServices.Contains(ServiceKind.SteamStore), "default includes Steam");
        Equal(false, defaults.AutomaticOptimization, "performance optimization defaults off");
        Equal(false, defaults.BrowserConversationVerification, "browser proof defaults off");
        defaults.FirstRunComplete = true;
        defaults.RequiredServices = new List<ServiceKind> { ServiceKind.ChatGPT, ServiceKind.GitHub };
        defaults.AutomaticOptimization = true;
        defaults.BrowserConversationVerification = true;
        store.Save(defaults);
        UserPreferences loaded = store.Load();
        Equal(true, loaded.FirstRunComplete, "first run persisted");
        Equal("ChatGPT,Gemini,GitHub", String.Join(",", loaded.RequiredServices),
            "saving one AI service keeps the fixed ChatGPT and Gemini core together");
        Equal(true, loaded.AutomaticOptimization, "advanced optimization persisted");
        Equal(false, loaded.BrowserConversationVerification, "removed browser consent is not persisted");
        Equal(true, File.ReadAllText(path).Contains("version=3"), "preference schema upgraded");
        Equal(true, File.ReadAllText(path).Contains("version=3"), "browser-free preference schema is version 3");
        Equal(false, File.ReadAllText(path).Contains("browserConversation"), "saved preferences contain no browser consent");
        File.WriteAllText(path, "broken", Encoding.UTF8);
        Equal(true, store.Load().RequiredServices.Contains(ServiceKind.Gemini), "corrupt preferences use safe defaults");
        Equal(1, Directory.GetFiles(root, "preferences.state.corrupt-*").Length, "corrupt preferences archived");

        string migrationRoot = Path.Combine(Path.GetTempPath(), "monitor-prefs-migration-" + Guid.NewGuid().ToString("N"));
        string migrationPath = Path.Combine(migrationRoot, "preferences.state");
        Directory.CreateDirectory(migrationRoot);
        File.WriteAllText(migrationPath,
            "version=1\r\nfirstRun=True\r\nautomatic=True\r\nservices=ChatGPT,JMComicWeb,GitHub\r\n", Encoding.UTF8);
        var migrationStore = new UserPreferenceStore(migrationPath);
        UserPreferences migrated = migrationStore.Load();
        Equal("ChatGPT,Gemini,GitHub", String.Join(",", migrated.RequiredServices),
            "legacy preferences migrate to the fixed ChatGPT and Gemini core");
        Equal(false, migrated.AutomaticOptimization, "v1 optimization migrates to conservative mode");
        Equal(false, migrated.BrowserConversationVerification, "v1 browser proof requires consent");
        Equal(0, Directory.GetFiles(migrationRoot, "preferences.state.corrupt-*").Length,
            "legacy jmcomic preference is migration not corruption");
        migrationStore.Save(migrated);
        Equal(false, File.ReadAllText(migrationPath).Contains("JMComic"), "saving removes legacy jmcomic value");
        File.WriteAllText(migrationPath,
            "version=2\r\nfirstRun=True\r\nautomatic=True\r\nbrowserConversation=True\r\nservices=GitHub\r\n", Encoding.UTF8);
        UserPreferences browserLegacy = migrationStore.Load();
        Equal(true, browserLegacy.AutomaticOptimization, "version 2 browser consent is ignored without corrupting preferences");
        Equal("ChatGPT,Gemini,GitHub", String.Join(",", browserLegacy.RequiredServices),
            "version 2 services migrate to the fixed ChatGPT and Gemini core");

        using (var form = new DetailsForm(new UserPreferences {
            RequiredServices = new List<ServiceKind> { ServiceKind.Gemini, ServiceKind.GitHub }
        }, delegate { }, delegate { }, delegate { }, delegate { }, delegate { }, delegate { }, delegate { }))
        {
            CheckBox core = FindCheckBox(form, "ChatGPT 与 Gemini（固定核心）");
            Equal(true, core != null && core.Checked && !core.Enabled,
                "settings display the AI intersection as one always-enabled core");
            Equal(null, FindCheckBox(form, "ChatGPT"), "settings cannot disable ChatGPT independently");
            Equal(null, FindCheckBox(form, "Gemini"), "settings cannot disable Gemini independently");
        }
    }

    private static void MonitorCoordinatorBehavior()
    {
        var runner = new BlockingCycleRunner();
        using (var coordinator = new MonitorCoordinator(runner, TimeSpan.FromHours(1), TimeSpan.FromSeconds(2)))
        {
            coordinator.Start();
            Equal(true, runner.WaitUntilEntered(1000), "coordinator starts initial check");
            Equal(MonitorRunState.Checking, coordinator.Latest.State, "inflight cycle displays checking");
            coordinator.RequestCheck();
            coordinator.RequestCheck();
            runner.Release();
            Equal(true, runner.WaitForRunCount(2, 1000), "coalesced follow-up runs");
            Thread.Sleep(80);
            Equal(2, runner.RunCount, "duplicate checks coalesced");
            coordinator.SetPaused(true);
            coordinator.RequestCheck();
            Thread.Sleep(80);
            Equal(2, runner.RunCount, "paused coordinator does not scan");
        }

        var stuckRunner = new BlockingCycleRunner();
        using (var coordinator = new MonitorCoordinator(stuckRunner, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(80)))
        {
            coordinator.Start();
            Equal(true, stuckRunner.WaitUntilEntered(1000), "watchdog cycle started");
            Equal(true, SpinWait.SpinUntil(() => coordinator.Latest.State == MonitorRunState.Stuck, 1000), "watchdog publishes stuck state");
            stuckRunner.Release();
        }

        var pausedRunner = new BlockingCycleRunner();
        using (var coordinator = new MonitorCoordinator(pausedRunner, TimeSpan.FromHours(1), TimeSpan.FromSeconds(2)))
        {
            coordinator.Start();
            Equal(true, pausedRunner.WaitUntilEntered(1000), "pause test starts cycle");
            coordinator.SetPaused(true);
            pausedRunner.Release();
            Thread.Sleep(100);
            Equal(MonitorRunState.Paused, coordinator.Latest.State, "completed cycle cannot overwrite pause");
        }

        var degradedRunner = new DegradedCycleRunner();
        int attentionCount = 0;
        using (var coordinator = new MonitorCoordinator(degradedRunner, TimeSpan.FromHours(1), TimeSpan.FromSeconds(2)))
        {
            coordinator.AttentionRequired += delegate { Interlocked.Increment(ref attentionCount); };
            coordinator.Start();
            Equal(true, degradedRunner.WaitForRunCount(1, 1000), "degraded cycle completed");
            coordinator.RequestCheck();
            Equal(true, degradedRunner.WaitForRunCount(2, 1000), "repeated degraded cycle completed");
            Thread.Sleep(80);
            Equal(1, attentionCount, "duplicate attention suppressed");
        }

        var restorableRunner = new RestorableCycleRunner();
        using (var coordinator = new MonitorCoordinator(restorableRunner, TimeSpan.FromHours(1), TimeSpan.FromSeconds(2)))
        {
            coordinator.Start();
            Equal(true, restorableRunner.WaitForRunCount(1, 1000), "restore runner initial cycle");
            coordinator.RequestRestorePrevious();
            Equal(true, SpinWait.SpinUntil(() => restorableRunner.RestoreCount == 1, 1000), "restore request reaches cycle runner");
        }

        using (var coordinator = new MonitorCoordinator(new ThrowingRestoreRunner(), TimeSpan.FromHours(1), TimeSpan.FromSeconds(1)))
        {
            coordinator.RequestRestorePrevious();
            coordinator.Start();
            Equal(true, SpinWait.SpinUntil(() => coordinator.Latest.State == MonitorRunState.Degraded, 1000), "restore exception becomes visible failure");
            coordinator.RequestCheck();
            Equal(true, SpinWait.SpinUntil(() => coordinator.Latest.State == MonitorRunState.Running, 1000), "coordinator recovers after restore exception");
        }

        var triggeredRunner = new TriggeredCycleRunner();
        using (var coordinator = new MonitorCoordinator(triggeredRunner, TimeSpan.FromHours(1), TimeSpan.FromSeconds(2)))
        {
            coordinator.Start();
            Equal(true, triggeredRunner.WaitForRunCount(1, 1000), "trigger runner receives startup cycle");
            coordinator.RequestCheck();
            Equal(true, triggeredRunner.WaitForRunCount(2, 1000), "trigger runner receives requested cycle");
            Equal(true, triggeredRunner.WaitForRunCount(3, 1000), "trigger runner receives scheduled cycle");
            Equal("Startup,Requested,Scheduled", String.Join(",", triggeredRunner.Triggers()),
                "coordinator distinguishes startup, requested, and scheduled diagnostics");
        }

    }

    private static void BrowserVerificationUiBehavior()
    {
        Equal("自动实测当前节点", BrowserVerificationForm.ActionText(true, true),
            "online companion action");
        Equal("请先安装或打开浏览器伴侣", BrowserVerificationForm.ActionText(false, true),
            "offline companion action");
        Equal("请先完成当前节点检测", BrowserVerificationForm.ActionText(true, false),
            "missing current evidence action");
        Equal(false, BrowserVerificationForm.CanStart(false, true, true),
            "persistent browser verification requires consent");
        Equal(true, BrowserVerificationForm.CanStart(true, true, true),
            "consented online current node can start");
        Equal(true, BrowserVerificationForm.CanStartOnce(true, true),
            "one-time browser verification does not persist consent");
        Equal(false, BrowserVerificationForm.CanStartOnce(false, true),
            "one-time browser verification still needs companion");
    }

    private static void BrowserCoordinatorIntegrationBehavior()
    {
        DateTime now = new DateTime(2026, 9, 11, 4, 0, 0, DateTimeKind.Utc);
        var clock = new FakeClock { UtcNow = now };
        var runner = new BrowserVerificationCycleRunner(now);
        using (var coordinator = new MonitorCoordinator(runner, TimeSpan.FromHours(1),
            TimeSpan.FromSeconds(2), false, clock, new FixedChallengeSource("CCM-D1E5"),
            TimeSpan.FromMilliseconds(5)))
        {
            coordinator.UpdatePreferences(new UserPreferences {
                FirstRunComplete = true,
                AutomaticOptimization = false,
                BrowserConversationVerification = false,
                RequiredServices = new List<ServiceKind> { ServiceKind.ChatGPT, ServiceKind.Gemini }
            });
            coordinator.Start();
            Equal(true, runner.WaitForRunCount(1, 1000), "browser integration initial network scan");

            BrowserBridgeMessage ready = coordinator.HandleBrowserMessage(
                ValidBrowserRequest("hello", "coordinator-hello"));
            Equal("ready", ready.Type, "companion hello accepted");
            Equal(BrowserVerificationStatus.Ready, coordinator.Latest.BrowserStatus,
                "companion status published without changing network evidence");
            Equal(BrowserVerificationStart.Started, coordinator.StartBrowserVerification(true),
                "user starts automatic browser conversation verification");

            BrowserBridgeMessage run = coordinator.HandleBrowserMessage(
                ValidBrowserRequest("poll", "coordinator-poll-chatgpt"));
            Equal("run", run.Type, "browser poll returns frozen task");
            Equal("browser-node", run.Node, "browser task freezes current node");
            Equal("CCM-D1E5", run.Challenge, "browser task carries fresh challenge");
            Equal("https://chatgpt.com/", run.Url, "chatgpt task uses exact official url");

            BrowserBridgeMessage passed = BrowserResultRequest(run, "Passed", true);
            Equal("ack", coordinator.HandleBrowserMessage(passed).Type,
                "fresh matching browser pass is acknowledged");
            Equal(true, SpinWait.SpinUntil(() => runner.BrowserProofCount == 1, 1000),
                "passed browser result reaches proof runner once");
            Equal(ServiceKind.ChatGPT, runner.LastProofService,
                "passed browser result retains service identity");
            Equal(BrowserConversationProof.CurrentProtocolVersion, runner.LastProtocolVersion,
                "passed browser result retains protocol version");
            Equal(0, runner.FailureCount, "passed browser result is not failure");

            BrowserBridgeMessage gemini = coordinator.HandleBrowserMessage(
                ValidBrowserRequest("poll", "coordinator-poll-gemini"));
            Equal("Gemini", gemini.Service, "browser verification proceeds sequentially to gemini");
            BrowserBridgeMessage signIn = BrowserResultRequest(gemini, "SignInRequired", false);
            Equal("ack", coordinator.HandleBrowserMessage(signIn).Type,
                "sign-in-required result is acknowledged");
            Thread.Sleep(50);
            Equal(0, runner.FailureCount, "sign-in requirement is not attributed to node failure");
            Equal(BrowserVerificationStatus.SignInRequired, coordinator.Latest.BrowserStatus,
                "sign-in requirement is visible in monitor status");
        }

        var driftRunner = new BrowserVerificationCycleRunner(now);
        using (var coordinator = new MonitorCoordinator(driftRunner, TimeSpan.FromHours(1),
            TimeSpan.FromSeconds(2), false, clock, new FixedChallengeSource("CCM-E2F6"),
            TimeSpan.FromMilliseconds(5)))
        {
            coordinator.Start();
            Equal(true, driftRunner.WaitForRunCount(1, 1000), "browser drift initial scan");
            coordinator.HandleBrowserMessage(ValidBrowserRequest("hello", "drift-hello"));
            Equal(BrowserVerificationStart.Started, coordinator.StartBrowserVerification(true),
                "browser drift session starts");
            BrowserBridgeMessage run = coordinator.HandleBrowserMessage(
                ValidBrowserRequest("poll", "drift-poll"));
            driftRunner.Node = "externally-changed-node";
            coordinator.RequestCheck();
            Equal(true, driftRunner.WaitForRunCount(2, 1000), "new network snapshot published before browser result");
            Equal("error", coordinator.HandleBrowserMessage(BrowserResultRequest(run, "Passed", true)).Type,
                "browser proof rejects node and exit drift");
            Equal(0, driftRunner.BrowserProofCount, "drifted browser proof never reaches runner");
            Equal("externally-changed-node", coordinator.Latest.ActualNode,
                "stale browser event cannot overwrite newer network evidence");
            Equal(BrowserVerificationStart.Started, coordinator.StartBrowserVerification(true),
                "drift aborts old browser session so a fresh one can start");
        }

        var failureRunner = new BrowserVerificationCycleRunner(now);
        using (var coordinator = new MonitorCoordinator(failureRunner, TimeSpan.FromHours(1),
            TimeSpan.FromSeconds(2), false, clock, new FixedChallengeSource("CCM-F3A7"),
            TimeSpan.FromMilliseconds(5)))
        {
            coordinator.Start();
            Equal(true, failureRunner.WaitForRunCount(1, 1000), "browser failure initial scan");
            coordinator.HandleBrowserMessage(ValidBrowserRequest("hello", "failure-hello"));
            coordinator.StartBrowserVerification(true);
            BrowserBridgeMessage run = coordinator.HandleBrowserMessage(
                ValidBrowserRequest("poll", "failure-poll"));
            coordinator.HandleBrowserMessage(BrowserResultRequest(run, "ConversationError", true));
            Equal(true, SpinWait.SpinUntil(() => failureRunner.FailureCount == 1, 1000),
                "sent conversation error reaches failure handling once");
        }
    }

    private static BrowserBridgeMessage BrowserResultRequest(BrowserBridgeMessage task,
        string outcome, bool messageSent)
    {
        return new BrowserBridgeMessage {
            Type = "result",
            ProtocolVersion = BrowserConversationProof.CurrentProtocolVersion,
            RequestId = "result-" + task.TaskId.Substring(0, 8),
            Browser = "Chrome",
            ExtensionVersion = "0.6.2",
            TaskId = task.TaskId,
            Service = task.Service,
            Challenge = task.Challenge,
            Outcome = outcome,
            MessageSent = messageSent,
            ElapsedMilliseconds = 1200
        };
    }

    private sealed class ThrowingRestoreRunner : IRestorableCycleRunner
    {
        public bool RestorePrevious() { throw new IOException("restore failed"); }
        public MonitorSnapshot Run(UserPreferences preferences)
        { return MonitorSnapshot.CreateState(MonitorRunState.Running, "ok", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1)); }
    }

    private sealed class BlockingCycleRunner : IMonitorCycleRunner
    {
        private readonly ManualResetEventSlim entered = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim released = new ManualResetEventSlim(false);
        private int runCount;
        public int RunCount { get { return Volatile.Read(ref runCount); } }
        public MonitorSnapshot Run(UserPreferences preferences)
        {
            int count = Interlocked.Increment(ref runCount);
            entered.Set();
            if (count == 1) released.Wait();
            return MonitorSnapshot.CreateState(MonitorRunState.Running, "完成", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));
        }
        public bool WaitUntilEntered(int milliseconds) { return entered.Wait(milliseconds); }
        public bool WaitForRunCount(int expected, int milliseconds)
        {
            return SpinWait.SpinUntil(() => RunCount >= expected, milliseconds);
        }
        public void Release() { released.Set(); }
    }

    private sealed class TriggeredCycleRunner : ITriggeredCycleRunner
    {
        private readonly object gate = new object();
        private readonly List<MonitorCycleTrigger> triggers = new List<MonitorCycleTrigger>();
        public MonitorSnapshot Run(UserPreferences preferences)
        { return Run(preferences, MonitorCycleTrigger.Scheduled); }
        public MonitorSnapshot Run(UserPreferences preferences, MonitorCycleTrigger trigger)
        {
            int count;
            lock (gate) { triggers.Add(trigger); count = triggers.Count; }
            DateTime next = count == 2 ? DateTime.UtcNow.AddMilliseconds(80) : DateTime.UtcNow.AddHours(1);
            return MonitorSnapshot.CreateState(MonitorRunState.Running, "ok", DateTime.UtcNow, next);
        }
        public bool WaitForRunCount(int count, int milliseconds)
        { return SpinWait.SpinUntil(() => { lock (gate) return triggers.Count >= count; }, milliseconds); }
        public MonitorCycleTrigger[] Triggers() { lock (gate) return triggers.ToArray(); }
    }

    private sealed class DegradedCycleRunner : IMonitorCycleRunner
    {
        private int runCount;
        public MonitorSnapshot Run(UserPreferences preferences)
        {
            Interlocked.Increment(ref runCount);
            return MonitorSnapshot.CreateState(MonitorRunState.Degraded, "same failure", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));
        }
        public bool WaitForRunCount(int expected, int milliseconds)
        {
            return SpinWait.SpinUntil(() => Volatile.Read(ref runCount) >= expected, milliseconds);
        }
    }

    private sealed class RestorableCycleRunner : IRestorableCycleRunner
    {
        private int runCount;
        private int restoreCount;
        public int RestoreCount { get { return Volatile.Read(ref restoreCount); } }
        public MonitorSnapshot Run(UserPreferences preferences)
        {
            Interlocked.Increment(ref runCount);
            return MonitorSnapshot.CreateState(MonitorRunState.Running, "完成", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));
        }
        public bool RestorePrevious()
        {
            Interlocked.Increment(ref restoreCount);
            return true;
        }
        public bool WaitForRunCount(int expected, int milliseconds)
        {
            return SpinWait.SpinUntil(() => Volatile.Read(ref runCount) >= expected, milliseconds);
        }
    }

    private sealed class BrowserVerificationCycleRunner : IAccountVerificationRunner
    {
        private readonly DateTime now;
        private int runCount;
        private int browserProofCount;
        private int failureCount;

        public BrowserVerificationCycleRunner(DateTime now)
        {
            this.now = now;
            Node = "browser-node";
        }

        public string Node { get; set; }
        public int BrowserProofCount { get { return Volatile.Read(ref browserProofCount); } }
        public int FailureCount { get { return Volatile.Read(ref failureCount); } }
        public ServiceKind LastProofService { get; private set; }
        public int LastProtocolVersion { get; private set; }

        public MonitorSnapshot Run(UserPreferences preferences)
        {
            Interlocked.Increment(ref runCount);
            return MonitorSnapshot.CreateRunning(Node,
                new CandidateScanResult(Node, CandidateHealth.Compatible, null, "ok", 100, 2,
                    new Dictionary<ServiceKind, ProbeResult> {
                        { ServiceKind.ChatGPT, ProbeResult.Success(50) },
                        { ServiceKind.Gemini, ProbeResult.Success(50) }
                    }, Node == "browser-node" ? "browser-exit" : "changed-exit", "JP"),
                null, "完成", now, now.AddMinutes(1));
        }

        public bool RecordBrowserConversationProof(string node, string exitFingerprint,
            ServiceKind service, DateTime verifiedUtc, int protocolVersion)
        {
            LastProofService = service;
            LastProtocolVersion = protocolVersion;
            Interlocked.Increment(ref browserProofCount);
            return true;
        }

        public void ReportServiceFailure(string node, ServiceKind service, DateTime reportedUtc)
        {
            Interlocked.Increment(ref failureCount);
        }

        public void ReportBrowserConversationFailure(string node, string exitFingerprint, ServiceKind service,
            BrowserVerificationOutcome outcome, bool messageSent, DateTime reportedUtc)
        {
            Interlocked.Increment(ref failureCount);
        }

        public bool WaitForRunCount(int expected, int milliseconds)
        {
            return SpinWait.SpinUntil(() => Volatile.Read(ref runCount) >= expected, milliseconds);
        }
    }

    private static void StatusReporting()
    {
        DateTime now = new DateTime(2026, 9, 7, 8, 0, 0, DateTimeKind.Utc);
        string report = StatusReport.Format(now, "台湾 T1", CandidateHealth.BasicCompatible, 82.3,
            "保持当前节点", "AI 登录待确认");
        Equal(true, report.Contains("版本：0.7.0-preview.8"), "status shows version");
        Equal(true, report.Contains("实际节点：台湾 T1"), "status shows leaf node");
        Equal(true, report.Contains("综合分：82.3"), "status shows score");
        Equal(true, report.Contains("决定：保持当前节点"), "status shows decision");
        Equal(true, report.Contains("AI 地区规则：ChatGPT ∩ Gemini 官方支持地区（快照 2026-09-17）"),
            "status identifies the dated shared AI region policy");
        Equal(false, report.Contains("secret"), "status omits credentials");
        Equal(true, StatusReport.Format(now, "日本 J1", CandidateHealth.Compatible, 88,
            "保持当前节点", "ok").Contains("检测状态：登录链路及后台服务探测通过"),
            "status does not mislabel anonymous login-chain evidence as account proof");
        Equal("当前节点可用，保守模式不进行性能寻优",
            StatusReport.DecisionText(SwitchModePolicy.ConservativeHoldReason),
            "status explains conservative hold reason");

        string translated = StatusReport.Format(now, "新加坡 S1", CandidateHealth.BasicCompatible, 74.8,
            "quality difference below threshold", "AI 登录待确认");
        Equal(true, translated.Contains("决定：质量提升不足 20%，保持当前节点"), "status translates controller decision");
        Equal(false, translated.Contains("quality difference below threshold"), "status omits raw English decision");
        Equal("候选节点尚未积累 5 次、跨度 30 分钟且成功率不低于 95% 的历史", StatusReport.DecisionText("target requires proven stability"), "status explains conservative quality gate");
        Equal("当前节点响应不超过 800 ms，保持当前节点", StatusReport.DecisionText("current response already preferred"), "status explains good-enough latency hold");
        Equal("候选节点响应超过自动寻优标准，不进行性能切换", StatusReport.DecisionText("target response exceeds preferred threshold"), "status explains candidate latency gate");
        Equal("当前节点最近 3 次中位响应未超过 800 ms，保持当前节点", StatusReport.DecisionText("current response not persistently slow"), "status explains latency hysteresis");
        Equal("候选节点最近 5 次延迟未达到中位数不超过 800 ms、单次不超过 1500 ms 的标准", StatusReport.DecisionText("target response history not preferred"), "status explains robust candidate latency gate");
        Equal("候选节点存在超过 1500 ms 的服务响应", StatusReport.DecisionText("target service response exceeds limit"), "status explains per-service latency gate");
        Equal("候选节点尚未完成所选 AI 服务的真实对话验证，不进行性能切换", StatusReport.DecisionText("target requires account verification"), "status explains account proof gate");
        Equal("当前节点故障，临时切换到网络链路已通过但尚未完成真实对话验证的节点", StatusReport.DecisionText("provisional emergency failover"), "status explains provisional emergency target");
    }
}
