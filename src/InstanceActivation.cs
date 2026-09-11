using System;
using System.Threading;

public sealed class InstanceActivation : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activationEvent;
    private RegisteredWaitHandle registration;
    private int disposed;

    private InstanceActivation(Mutex mutex, EventWaitHandle activationEvent, bool isOwner)
    {
        this.mutex = mutex;
        this.activationEvent = activationEvent;
        IsOwner = isOwner;
    }

    public bool IsOwner { get; private set; }
    public event Action Activated;

    public static InstanceActivation TryOwn(string id)
    {
        if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("An instance id is required.", "id");
        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".Activate");
        bool owns;
        var mutex = new Mutex(true, id, out owns);
        var result = new InstanceActivation(mutex, activationEvent, owns);
        if (!owns) activationEvent.Set();
        return result;
    }

    public void StartListening()
    {
        if (!IsOwner) throw new InvalidOperationException("Only the owning instance can listen for activation.");
        if (registration != null) return;
        registration = ThreadPool.RegisterWaitForSingleObject(activationEvent, delegate {
            Action handler = Activated;
            if (handler != null) handler();
        }, null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (registration != null) registration.Unregister(null);
        if (IsOwner) mutex.ReleaseMutex();
        mutex.Dispose();
        activationEvent.Dispose();
    }
}
