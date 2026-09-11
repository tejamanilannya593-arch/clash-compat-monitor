using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public static class BoundedPipeIo
{
    public static byte[] ReadAll(Stream stream, TimeSpan timeout, int maximumBytes)
    {
        if (stream == null) throw new ArgumentNullException("stream");
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("timeout");
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException("maximumBytes");
        return Execute(stream, timeout, delegate {
            using (var output = new MemoryStream())
            {
                var buffer = new byte[4096];
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return output.ToArray();
                    if (output.Length + read > maximumBytes)
                        throw new InvalidDataException("Mihomo response exceeded the allowed size.");
                    output.Write(buffer, 0, read);
                }
            }
        });
    }

    public static void WriteAll(Stream stream, byte[] header, byte[] body, TimeSpan timeout)
    {
        if (stream == null) throw new ArgumentNullException("stream");
        if (header == null) throw new ArgumentNullException("header");
        if (body == null) throw new ArgumentNullException("body");
        Execute(stream, timeout, delegate {
            stream.Write(header, 0, header.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
            return true;
        });
    }

    private static T Execute<T>(Stream stream, TimeSpan timeout, Func<T> operation)
    {
        Task<T> task = Task.Factory.StartNew(operation, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            if (task.Wait(timeout)) return task.Result;
        }
        catch (AggregateException error)
        {
            throw error.InnerException;
        }

        stream.Dispose();
        try { task.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        throw new TimeoutException("Mihomo pipe operation timed out.");
    }
}
