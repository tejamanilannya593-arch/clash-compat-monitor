using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

public sealed class ThroughputResult
{
    public ThroughputResult(long bytesRead, long elapsedMilliseconds)
    {
        BytesRead = bytesRead;
        ElapsedMilliseconds = Math.Max(1, elapsedMilliseconds);
        BytesPerSecond = bytesRead * 1000.0 / ElapsedMilliseconds;
    }
    public long BytesRead { get; private set; }
    public long ElapsedMilliseconds { get; private set; }
    public double BytesPerSecond { get; private set; }
}

public sealed class ThroughputBudget
{
    private long reserved;
    public ThroughputBudget(long maximumBytes) { MaximumBytes = maximumBytes; }
    public long MaximumBytes { get; private set; }
    public bool TryReserve(long bytes)
    {
        if (bytes <= 0) return false;
        while (true)
        {
            long before = System.Threading.Interlocked.Read(ref reserved);
            if (before > MaximumBytes - bytes) return false;
            if (System.Threading.Interlocked.CompareExchange(ref reserved, before + bytes, before) == before) return true;
        }
    }
}

public sealed class ThroughputProbe : IDisposable
{
    public const int SampleBytes = 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri endpoint;

    public ThroughputProbe(string proxyUrl, string endpointUrl)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        client = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(proxyUrl), UseProxy = true, UseCookies = false });
        client.Timeout = TimeSpan.FromSeconds(5);
        endpoint = new Uri(endpointUrl);
    }

    public ThroughputResult Probe()
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using (HttpResponseMessage response = client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode) return new ThroughputResult(0, timer.ElapsedMilliseconds);
                using (Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    return Measure(stream, SampleBytes, timer);
            }
        }
        catch (HttpRequestException) { return new ThroughputResult(0, timer.ElapsedMilliseconds); }
        catch (System.Threading.Tasks.TaskCanceledException) { return new ThroughputResult(0, timer.ElapsedMilliseconds); }
        catch (IOException) { return new ThroughputResult(0, timer.ElapsedMilliseconds); }
    }

    public static ThroughputResult Measure(Stream stream, int maximumBytes, long elapsedMilliseconds)
    {
        var timer = Stopwatch.StartNew();
        long count = ReadLimited(stream, maximumBytes);
        return new ThroughputResult(count, elapsedMilliseconds > 0 ? elapsedMilliseconds : timer.ElapsedMilliseconds);
    }

    private static ThroughputResult Measure(Stream stream, int maximumBytes, Stopwatch timer)
    {
        long count = ReadLimited(stream, maximumBytes);
        return new ThroughputResult(count, timer.ElapsedMilliseconds);
    }

    private static long ReadLimited(Stream stream, int maximumBytes)
    {
        var buffer = new byte[32 * 1024];
        long total = 0;
        while (total < maximumBytes)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maximumBytes - total));
            if (read <= 0) break;
            RunStatistics.AddBodyBytes(read);
            total += read;
        }
        return total;
    }

    public void Dispose() { client.Dispose(); }
}
