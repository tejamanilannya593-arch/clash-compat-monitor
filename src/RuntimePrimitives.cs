using System;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow { get { return DateTime.UtcNow; } }
}

// Retained as an injection seam for compatibility with existing worker construction.
public interface ITrafficMeter
{
    long GetTotalBytes();
}

// Persisted legacy incident records are retained as data, but no longer influence switching.
public sealed class ServiceIncidentRecord
{
    public ServiceKind Service { get; set; }
    public ProbeFailureKind FailureKind { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UntilUtc { get; set; }
}
