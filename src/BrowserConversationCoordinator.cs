using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

public interface IChallengeSource
{
    string Create();
}

public sealed class CryptographicChallengeSource : IChallengeSource
{
    public string Create()
    {
        var bytes = new byte[16];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return "CCM-" + BitConverter.ToString(bytes).Replace("-", "");
    }
}

public enum BrowserVerificationStart
{
    Started,
    ConsentRequired,
    CompanionOffline,
    NoEligibleServices,
    Busy
}

public enum BrowserVerificationAcceptance
{
    Accepted,
    Replayed,
    Expired,
    Drifted,
    Invalid
}

public enum BrowserVerificationOutcome
{
    Passed,
    ConversationError,
    GenerationTimeout,
    SignInRequired,
    ChallengeRequired,
    AutomationUnsupported,
    Cancelled
}

public sealed class BrowserVerificationTask
{
    public string TaskId { get; internal set; }
    public ServiceKind Service { get; internal set; }
    public string Node { get; internal set; }
    public string ExitFingerprint { get; internal set; }
    public string Challenge { get; internal set; }
    public DateTime CreatedUtc { get; internal set; }
    public DateTime ExpiresUtc { get; internal set; }
    public bool UserInitiated { get; internal set; }
}

public sealed class BrowserVerificationResult
{
    public string TaskId { get; set; }
    public ServiceKind Service { get; set; }
    public string Challenge { get; set; }
    public BrowserVerificationOutcome Outcome { get; set; }
    public bool MessageSent { get; set; }
    public long ElapsedMilliseconds { get; set; }
}

public sealed class BrowserVerificationSession
{
    internal BrowserVerificationSession(MonitorSnapshot snapshot, IEnumerable<ServiceKind> services,
        bool userInitiated)
    {
        Snapshot = snapshot;
        RemainingServices = new Queue<ServiceKind>(services);
        UserInitiated = userInitiated;
    }

    internal MonitorSnapshot Snapshot { get; private set; }
    internal Queue<ServiceKind> RemainingServices { get; private set; }
    internal BrowserVerificationTask ActiveTask { get; set; }
    internal bool UserInitiated { get; private set; }
    public string Node { get { return Snapshot == null ? "" : Snapshot.ActualNode; } }
    public string ExitFingerprint { get { return Snapshot == null ? "" : Snapshot.ExitFingerprint; } }
}

public sealed class BrowserConversationCoordinator
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TaskLifetime = TimeSpan.FromMinutes(5);
    private readonly object gate = new object();
    private readonly IClock clock;
    private readonly IChallengeSource challenges;
    private readonly HashSet<string> completed = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<ServiceKind, DateTime> cooldowns = new Dictionary<ServiceKind, DateTime>();
    private BrowserVerificationSession session;
    private DateTime lastHeartbeatUtc = DateTime.MinValue;
    private bool consent;

    public BrowserConversationCoordinator(IClock clock, IChallengeSource challenges)
    {
        if (clock == null) throw new ArgumentNullException("clock");
        if (challenges == null) throw new ArgumentNullException("challenges");
        this.clock = clock;
        this.challenges = challenges;
    }

    public BrowserVerificationResult LastAcceptedResult { get; private set; }
    public string LastBrowser { get; private set; }

    public void SetConsent(bool value)
    {
        lock (gate) consent = value;
    }

    public void ObserveCompanion(string browser, DateTime now)
    {
        if (String.IsNullOrWhiteSpace(browser)) return;
        lock (gate)
        {
            LastBrowser = browser.Trim();
            lastHeartbeatUtc = now;
        }
    }

    public bool IsCompanionOnline(DateTime now)
    {
        lock (gate)
            return lastHeartbeatUtc <= now && lastHeartbeatUtc > now.Subtract(OnlineWindow);
    }

    public BrowserVerificationStart StartCurrent(MonitorSnapshot snapshot,
        IEnumerable<ServiceKind> services, bool userInitiated)
    {
        lock (gate)
        {
            if (session != null) return BrowserVerificationStart.Busy;
            if (!userInitiated && !consent) return BrowserVerificationStart.ConsentRequired;
            DateTime now = clock.UtcNow;
            if (!(lastHeartbeatUtc <= now && lastHeartbeatUtc > now.Subtract(OnlineWindow)))
                return BrowserVerificationStart.CompanionOffline;
            if (snapshot == null || String.IsNullOrWhiteSpace(snapshot.ActualNode) ||
                String.IsNullOrWhiteSpace(snapshot.ExitFingerprint))
                return BrowserVerificationStart.NoEligibleServices;

            List<ServiceKind> selected = (services ?? Enumerable.Empty<ServiceKind>())
                .Where(IsAiService).Distinct().Where(service => userInitiated || !IsCoolingDown(service, now)).ToList();
            if (selected.Count == 0) return BrowserVerificationStart.NoEligibleServices;
            session = new BrowserVerificationSession(snapshot, selected, userInitiated);
            return BrowserVerificationStart.Started;
        }
    }

    public BrowserVerificationTask Poll(string browser, DateTime now)
    {
        ObserveCompanion(browser, now);
        lock (gate)
        {
            if (session == null) return null;
            if (session.ActiveTask != null)
            {
                if (session.ActiveTask.ExpiresUtc > now) return session.ActiveTask;
                completed.Add(session.ActiveTask.TaskId);
                SetCooldown(session.ActiveTask.Service, now);
                session.ActiveTask = null;
            }
            if (session.RemainingServices.Count == 0)
            {
                session = null;
                return null;
            }

            string challenge = challenges.Create();
            if (String.IsNullOrWhiteSpace(challenge)) throw new InvalidOperationException("Challenge source returned no value.");
            session.ActiveTask = new BrowserVerificationTask {
                TaskId = Guid.NewGuid().ToString("N"),
                Service = session.RemainingServices.Dequeue(),
                Node = session.Node,
                ExitFingerprint = session.ExitFingerprint,
                Challenge = challenge,
                CreatedUtc = now,
                ExpiresUtc = now.Add(TaskLifetime),
                UserInitiated = session.UserInitiated
            };
            return session.ActiveTask;
        }
    }

    public BrowserVerificationAcceptance Accept(BrowserVerificationResult result,
        MonitorSnapshot currentSnapshot, DateTime now)
    {
        lock (gate)
        {
            if (result == null || String.IsNullOrWhiteSpace(result.TaskId))
                return BrowserVerificationAcceptance.Invalid;
            if (completed.Contains(result.TaskId)) return BrowserVerificationAcceptance.Replayed;
            BrowserVerificationTask active = session == null ? null : session.ActiveTask;
            if (active == null || !String.Equals(active.TaskId, result.TaskId, StringComparison.Ordinal) ||
                active.Service != result.Service || !String.Equals(active.Challenge, result.Challenge, StringComparison.Ordinal) ||
                !Enum.IsDefined(typeof(BrowserVerificationOutcome), result.Outcome))
                return BrowserVerificationAcceptance.Invalid;
            if (active.ExpiresUtc <= now)
            {
                CompleteActive(active, now, true);
                return BrowserVerificationAcceptance.Expired;
            }
            if (currentSnapshot == null ||
                !String.Equals(active.Node, currentSnapshot.ActualNode, StringComparison.Ordinal) ||
                !String.Equals(active.ExitFingerprint, currentSnapshot.ExitFingerprint, StringComparison.Ordinal))
            {
                completed.Add(active.TaskId);
                session = null;
                return BrowserVerificationAcceptance.Drifted;
            }

            LastAcceptedResult = result;
            CompleteActive(active, now, result.Outcome != BrowserVerificationOutcome.Passed);
            return BrowserVerificationAcceptance.Accepted;
        }
    }

    public static bool IsRollbackEligible(BrowserVerificationResult result)
    {
        return result != null && result.MessageSent &&
            (result.Outcome == BrowserVerificationOutcome.ConversationError ||
             result.Outcome == BrowserVerificationOutcome.GenerationTimeout);
    }

    public bool Cancel(string taskId)
    {
        lock (gate)
        {
            BrowserVerificationTask active = session == null ? null : session.ActiveTask;
            if (active == null || !String.Equals(active.TaskId, taskId, StringComparison.Ordinal)) return false;
            completed.Add(active.TaskId);
            session = null;
            return true;
        }
    }

    private void CompleteActive(BrowserVerificationTask active, DateTime now, bool failed)
    {
        completed.Add(active.TaskId);
        if (failed) SetCooldown(active.Service, now);
        session.ActiveTask = null;
        if (session.RemainingServices.Count == 0) session = null;
    }

    private bool IsCoolingDown(ServiceKind service, DateTime now)
    {
        DateTime until;
        return cooldowns.TryGetValue(service, out until) && until > now;
    }

    private void SetCooldown(ServiceKind service, DateTime now)
    {
        cooldowns[service] = now.Add(BrowserConversationProof.AutomaticFailureCooldown);
    }

    private static bool IsAiService(ServiceKind service)
    {
        return service == ServiceKind.ChatGPT || service == ServiceKind.Gemini;
    }
}
