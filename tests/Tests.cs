using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    public static int Main()
    {
        Equal("ClashCompatibilityMonitor", MonitorIdentity.Name, "identity");
        Equal("0.6.2", MonitorIdentity.Version, "release version");
        Equal(TimeSpan.FromMinutes(30), MonitorConfiguration.CreateDefault().ReloadRecoveryFreshness, "reload recovery freshness");
        CandidateFiltering();
        PipeHttpDecoding();
        BoundedPipeBehavior();
        MihomoPipeIntegration();
        CompatibilityScanning();
        QualityScoringAndState();
        ThroughputAndTraffic();
        StabilityAndState();
        RuntimeGuards();
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

    private static void ServiceEvidenceBehavior()
    {
        var strict = new CandidateScanResult("strict", CandidateHealth.Compatible, null, "ok");
        var reachable = new CandidateScanResult("reachable", CandidateHealth.BasicCompatible, null, "challenge");
        Equal(true, ServiceEvidencePolicy.CanHold(strict), "strict scan can hold");
        Equal(true, ServiceEvidencePolicy.CanHold(reachable), "reachable scan can hold without churn");
        Equal(true, ServiceEvidencePolicy.CanEmergencySwitch(strict), "strict scan can be emergency target");
        Equal(false, ServiceEvidencePolicy.CanEmergencySwitch(reachable), "reachable-only scan cannot be emergency target");

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

        ExitIdentity identity = ExitIdentityParser.Parse("ip=203.0.113.8\nloc=JP\ncolo=NRT\n", new byte[] { 1, 2, 3 });
        Equal("JP", identity.CountryCode, "trace country parsed");
        Equal(false, identity.Fingerprint.Contains("203.0.113.8"), "fingerprint hides raw exit IP");
        Equal(identity.Fingerprint,
            ExitIdentityParser.Parse("ip=203.0.113.8\nloc=JP\n", new byte[] { 1, 2, 3 }).Fingerprint,
            "same exit and key have stable fingerprint");

        var probe = new FakeProbe { DefaultResult = ProbeResult.Success(100) };
        CandidateScanResult supported = new CompatibilityScanner(new FakeMihomo(), probe, "probe",
            new FakeExitIdentityProbe(new ExitIdentity("exit-jp", "JP", "ok")))
            .ScanSelected(new CandidateNode("香港名称但日本出口", 1), new[] { ServiceKind.ChatGPT });
        Equal(CandidateHealth.Compatible, supported.Health, "actual supported exit wins over node label");
        Equal("JP", supported.ExitCountryCode, "scan carries actual exit country");
        Equal("exit-jp", supported.ExitFingerprint, "scan carries encrypted exit fingerprint");
        Equal("exit-jp", MonitorSnapshot.CreateRunning(supported.Name, supported, null, "ok",
            DateTime.UtcNow, DateTime.UtcNow).ExitFingerprint, "snapshot carries exit fingerprint for user verification");

        CandidateScanResult unsupported = new CompatibilityScanner(new FakeMihomo(), probe, "probe",
            new FakeExitIdentityProbe(new ExitIdentity("exit-hk", "HK", "ok")))
            .ScanSelected(new CandidateNode("日本名称但香港出口", 1), new[] { ServiceKind.ChatGPT });
        Equal(CandidateHealth.RegionBlocked, unsupported.Health, "actual Hong Kong exit blocks ChatGPT switch evidence");
        Equal(ServiceKind.ChatGPT, unsupported.FailedService.Value, "region gate identifies ChatGPT");
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
        Equal(false, ServiceEvidencePolicy.CanQualitySwitch(strict, data, "scope",
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, now),
            "quality switch waits for every selected AI account proof");
        AccountVerificationMemory.MarkBrowserConversation(data, "scope", "node", "fingerprint", ServiceKind.Gemini,
            now, BrowserConversationProof.CurrentProtocolVersion);
        Equal(true, ServiceEvidencePolicy.CanQualitySwitch(strict, data, "scope",
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, now),
            "quality switch accepts current exit after both AI proofs");
        Equal(true, ServiceEvidencePolicy.CanQualitySwitch(strict, new ExperienceData(), "scope",
            new[] { ServiceKind.Google, ServiceKind.GitHub }, now),
            "non-AI configuration does not require account proof");
        Equal(false, ServiceEvidencePolicy.CanRestoreAfterReload(strict, new ExperienceData(), "scope",
            new[] { ServiceKind.ChatGPT, ServiceKind.Gemini }, now),
            "reload recovery cannot proactively select unverified AI target");
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
        Equal(TimeSpan.FromMinutes(3), policy.Interval(healthy), "stable connection reduces probe frequency");
        Equal(TimeSpan.FromSeconds(30), policy.Interval(bad), "failure restores fast checks");
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        policy.Begin("a", "b", 100, true);
        var restored = serializer.Deserialize<ConnectionAssurance>(serializer.Serialize(policy));
        Equal("b", restored.Target, "switch observation survives restart");
        Equal("a", restored.Previous, "rollback origin survives restart");
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
        Equal(CandidateHealth.Unknown, suppressedOwnFailure.Health, "suppressed endpoint clears its node failure attribution");
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
        ProbeResult result = kind == ProbeFailureKind.Region ? ProbeResult.RegionFailure("same", 20) :
            kind == ProbeFailureKind.Transient ? ProbeResult.TransientFailure("same", 20) : ProbeResult.ServiceFailure("same", 20);
        CandidateHealth health = kind == ProbeFailureKind.Region ? CandidateHealth.RegionBlocked :
            kind == ProbeFailureKind.Transient ? CandidateHealth.Transient : CandidateHealth.ServiceFailed;
        return new CandidateScanResult(node, health, service, "same", 20, 1,
            new Dictionary<ServiceKind, ProbeResult> { { service, result } });
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
                            : i == 2 ? "{\"delay\":86}" : i == 4 ? "{\"ipv6\":true}" : "";
                        byte[] payload = Encoding.UTF8.GetBytes(responseBody);
                        string response = i == 0
                            ? "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" + payload.Length.ToString("X") + "\r\n" + responseBody + "\r\n0\r\n\r\n"
                            : (i == 2 || i == 4) ? "HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length + "\r\n\r\n" + responseBody
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
        client.Select("组", "节点二");
        Equal(86, client.GetDelay("节点 一", "https://www.gstatic.com/generate_204", 5000), "mihomo delay");
        string configPath = Path.Combine(Path.GetTempPath(), "mihomo-ipv4-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(configPath, "ipv6: true\r\ndns:\r\n  ipv6: true\r\n", Encoding.UTF8);
        Equal(true, client.EnsureIpv4Compatibility(configPath), "mihomo ipv4 hot reload");
        Equal(true, client.IsRuntimeIpv6Enabled(), "mihomo runtime ipv6 query");
        server.Join(5000);
        Equal(null, serverError, "fake pipe server error");
        Equal(5, requests.Count, "pipe request count");
        Equal(true, requests[0].StartsWith("GET /proxies HTTP/1.1\r\n", StringComparison.Ordinal), "pipe get path");
        Equal(true, requests[0].Contains("Authorization: Bearer test-secret"), "pipe auth header");
        Equal(true, requests[1].StartsWith("PUT /proxies/%E7%BB%84 HTTP/1.1\r\n", StringComparison.Ordinal), "pipe put escaped path");
        Equal(true, requests[1].Contains("{\"name\":\"节点二\"}"), "pipe put body");
        Equal(true, requests[2].StartsWith("GET /proxies/%E8%8A%82%E7%82%B9%20%E4%B8%80/delay?", StringComparison.Ordinal), "delay escaped path");
        Equal(true, requests[3].StartsWith("PUT /configs?force=true HTTP/1.1\r\n", StringComparison.Ordinal), "config reload path");
        Equal(true, requests[3].Contains("ipv6: false"), "config reload disables ipv6");
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
        Equal(CandidateHealth.BasicCompatible, basic.Health, "basic AI reachability classified separately");
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
        Equal(1, probe.Calls.Count, "chatgpt fail fast count");

        probe = new FakeProbe();
        probe.Results[ServiceKind.Gemini] = ProbeResult.RegionFailure("unsupported");
        result = new CompatibilityScanner(mihomo, probe, "probe").Scan(new CandidateNode("node", 1), new ServiceKind[0]);
        Equal(CandidateHealth.RegionBlocked, result.Health, "gemini region classification");
        Equal(2, probe.Calls.Count, "gemini fail fast count");

        probe = new FakeProbe();
        result = new CompatibilityScanner(mihomo, probe, "probe").Scan(new CandidateNode("node", 1), new[] { ServiceKind.Discord, ServiceKind.Spotify });
        Equal(CandidateHealth.Compatible, result.Health, "all mandatory compatible");
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

        probe = new FakeProbe { DefaultResult = ProbeResult.Success(75) };
        scanner = new CompatibilityScanner(mihomo, probe, "probe",
            new FakeExitIdentityProbe(new ExitIdentity("selected-fp", "JP", "ok")));
        CandidateScanResult selected = scanner.ScanSelected(new CandidateNode("selected", 1),
            new[] { ServiceKind.ChatGPT, ServiceKind.GitHub });
        Equal(2, probe.Calls.Count, "only selected services probed");
        Equal(75L, selected.ServiceResults[ServiceKind.ChatGPT].ElapsedMilliseconds, "service latency retained");
        DateTime snapshotTime = new DateTime(2026, 9, 8, 8, 0, 0, DateTimeKind.Utc);
        MonitorSnapshot snapshot = MonitorSnapshot.CreateRunning("selected", selected, 82.5,
            "保持当前节点", snapshotTime, snapshotTime.AddMinutes(1));
        Equal("selected", snapshot.ActualNode, "snapshot leaf node");
        Equal(2, snapshot.Services.Count, "snapshot retains service evidence");
        MonitorPresentation view = MonitorPresentation.From(snapshot);
        Equal("部分服务待验证", view.StateText, "login-ready AI scan waits for account verification");
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
        Equal(true, StartupRecovery.NeedsImmediateConfirmation(new CandidateScanResult("x", CandidateHealth.Transient, ServiceKind.GitHub, "timeout")), "definite startup failure gets immediate confirmation");
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

    private sealed class FakeProbe : IServiceProbe
    {
        public readonly List<ServiceKind> Calls = new List<ServiceKind>();
        public readonly Dictionary<ServiceKind, ProbeResult> Results = new Dictionary<ServiceKind, ProbeResult>();
        public ProbeResult DefaultResult = ProbeResult.Success();
        public ProbeResult Probe(ServiceKind service, TimeSpan timeout)
        {
            Calls.Add(service);
            ProbeResult result;
            return Results.TryGetValue(service, out result) ? result : DefaultResult;
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
        Equal(500.0, QualityPolicy.PreferredResponseMilliseconds, "preferred HTTP response threshold");
        Equal("优秀", QualityPolicy.LatencyBand(500), "excellent latency band boundary");
        Equal("良好", QualityPolicy.LatencyBand(800), "good latency band boundary");
        Equal("可连接但偏慢", QualityPolicy.LatencyBand(1500), "connectable but slow latency band boundary");
        Equal("不适合自动寻优", QualityPolicy.LatencyBand(1501), "automatic optimization latency cutoff");
        Equal(false, QualityPolicy.CurrentNeedsOptimization(new[] { 900.0, 1000.0 }), "current latency needs three samples");
        Equal(false, QualityPolicy.CurrentNeedsOptimization(new[] { 700.0, 800.0, 900.0 }), "current median at 800 holds");
        Equal(true, QualityPolicy.CurrentNeedsOptimization(new[] { 700.0, 801.0, 900.0 }), "current median above 800 may optimize");
        Equal(true, QualityPolicy.CandidateLatencyIsPreferred(new[] { 300.0, 400.0, 500.0, 600.0, 800.0 }), "candidate median p95 and jitter pass");
        Equal(true, QualityPolicy.CandidateLatencyIsPreferred(new[] { 650.0, 700.0, 800.0, 900.0, 1500.0 }), "candidate median 800 and maximum 1500 pass");
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
        double[] preferredTarget = { 300, 400, 500, 600, 800 };
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
        Equal(2, loaded.RequiredServices.Count, "service selection persisted");
        Equal(true, loaded.AutomaticOptimization, "advanced optimization persisted");
        Equal(true, loaded.BrowserConversationVerification, "browser proof consent persisted");
        Equal(true, File.ReadAllText(path).Contains("version=2"), "preference schema upgraded");
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
        Equal("ChatGPT,GitHub", String.Join(",", migrated.RequiredServices), "legacy jmcomic preference ignored");
        Equal(false, migrated.AutomaticOptimization, "v1 optimization migrates to conservative mode");
        Equal(false, migrated.BrowserConversationVerification, "v1 browser proof requires consent");
        Equal(0, Directory.GetFiles(migrationRoot, "preferences.state.corrupt-*").Length,
            "legacy jmcomic preference is migration not corruption");
        migrationStore.Save(migrated);
        Equal(false, File.ReadAllText(migrationPath).Contains("JMComic"), "saving removes legacy jmcomic value");
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
        Equal(true, report.Contains("版本：0.6.2"), "status shows version");
        Equal(true, report.Contains("实际节点：台湾 T1"), "status shows leaf node");
        Equal(true, report.Contains("综合分：82.3"), "status shows score");
        Equal(true, report.Contains("决定：保持当前节点"), "status shows decision");
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
