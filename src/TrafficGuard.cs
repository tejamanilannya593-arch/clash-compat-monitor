using System;
using System.Net.NetworkInformation;
using System.Threading;

public interface ITrafficMeter { long GetTotalBytes(); }

public sealed class SystemTrafficMeter : ITrafficMeter
{
    public long GetTotalBytes()
    {
        long total = 0;
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
            total += stats.BytesReceived + stats.BytesSent;
        }
        return total;
    }
}

public sealed class TrafficGuard
{
    private readonly IClock clock;
    private readonly double thresholdBytesPerSecond;
    private readonly TimeSpan cooldown;
    private DateTime nextAllowedUtc = DateTime.MinValue;
    public TrafficGuard(IClock clock, double thresholdBytesPerSecond, TimeSpan cooldown)
    {
        this.clock = clock;
        this.thresholdBytesPerSecond = thresholdBytesPerSecond;
        this.cooldown = cooldown;
    }

    public bool MayProbe(double bytesPerSecond)
    {
        if (bytesPerSecond > thresholdBytesPerSecond)
        {
            nextAllowedUtc = clock.UtcNow.Add(cooldown);
            return false;
        }
        return clock.UtcNow >= nextAllowedUtc;
    }

    public bool SampleAndMayProbe(ITrafficMeter meter)
    {
        long before = meter.GetTotalBytes();
        Thread.Sleep(1000);
        long after = meter.GetTotalBytes();
        return MayProbe(Math.Max(0, after - before));
    }
}
