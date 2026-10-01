using System;
using System.Collections.Generic;

// Runtime state kept after removing the former proactive optimization state machine.
// JavaScriptSerializer ignores retired fields in older experience.json files.
public sealed class ConnectionAssurance
{
    public RecoveryState Recovery { get; set; }
    public string Scope { get; set; }
    public string Previous { get; set; }
    public string PreviousNodeId { get; set; }
    public List<AutomaticSwitchRecord> AutomaticSwitches { get; set; }

    public ConnectionAssurance()
    {
        Recovery = new RecoveryState();
        AutomaticSwitches = new List<AutomaticSwitchRecord>();
    }

    public void SetScope(string scope)
    {
        if (String.Equals(Scope, scope, StringComparison.Ordinal)) return;
        Scope = scope;
        Previous = null;
        PreviousNodeId = null;
        Recovery = new RecoveryState();
    }

    public void CaptureIdentities(IDictionary<string, string> nodeIdsByName)
    {
        string value;
        PreviousNodeId = nodeIdsByName != null && !String.IsNullOrWhiteSpace(Previous) &&
            nodeIdsByName.TryGetValue(Previous, out value) && NodeIdentity.IsStrong(value) ? value : null;
    }

    public void RecordAutomaticSwitch(string from, string to, string reason, DateTime now,
        ServiceKind? failureService = null)
    {
        if (AutomaticSwitches == null) AutomaticSwitches = new List<AutomaticSwitchRecord>();
        AutomaticSwitches.Add(new AutomaticSwitchRecord { Utc = now, From = from, To = to,
            Reason = reason, FailureService = failureService });
        AutomaticSwitches.RemoveAll(x => x == null || x.Utc < now.AddDays(-7));
        if (AutomaticSwitches.Count > 100)
            AutomaticSwitches.RemoveRange(0, AutomaticSwitches.Count - 100);
    }
}

public sealed class AutomaticSwitchRecord
{
    public DateTime Utc { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public string Reason { get; set; }
    public ServiceKind? FailureService { get; set; }
}
