using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class CandidateDelayMeasurement
{
    public static Dictionary<string, int> Measure(IMihomoClient mihomo,
        IEnumerable<CandidateNode> candidates, string url, int timeoutMilliseconds,
        int maximumConcurrency)
    {
        if (mihomo == null) throw new ArgumentNullException("mihomo");
        List<CandidateNode> nodes = (candidates ?? Enumerable.Empty<CandidateNode>())
            .Where(x => x != null && !String.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.Ordinal).Select(x => x.First()).ToList();
        int[] values = Enumerable.Repeat(Int32.MaxValue, nodes.Count).ToArray();
        int nextIndex = -1;
        Task[] workers = Enumerable.Range(0, Math.Min(maximumConcurrency, nodes.Count)).Select(worker =>
            Task.Factory.StartNew(() => {
                while (true)
                {
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= nodes.Count) return;
                    try
                    {
                        int delay = mihomo.GetDelay(nodes[index].Name, url, timeoutMilliseconds);
                        values[index] = delay > 0 ? delay : Int32.MaxValue;
                    }
                    catch (OutOfMemoryException) { throw; }
                    catch (StackOverflowException) { throw; }
                    catch (ThreadAbortException) { throw; }
                    catch (Exception) { values[index] = Int32.MaxValue; }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default)).ToArray();
        if (workers.Length > 0) Task.WaitAll(workers);
        return nodes.Select((node, index) => new { node.Name, Delay = values[index] })
            .ToDictionary(x => x.Name, x => x.Delay, StringComparer.Ordinal);
    }
}
