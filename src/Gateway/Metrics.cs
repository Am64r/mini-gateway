using System.Collections.Concurrent;
using System.Diagnostics;

namespace Gateway;

public static class Metrics
{
    private static readonly ConcurrentDictionary<string, RouteMetrics> RouteStats = new();
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    public static TimeSpan Uptime => DateTimeOffset.UtcNow - StartTime;

    public static Stopwatch StartRequest(string routePrefix)
    {
        RouteStats.GetOrAdd(routePrefix, _ => new RouteMetrics());
        return Stopwatch.StartNew();
    }

    public static void RecordSuccess(string routePrefix, Stopwatch sw)
    {
        sw.Stop();
        if (RouteStats.TryGetValue(routePrefix, out var metrics))
        {
            Interlocked.Increment(ref metrics.TotalRequests);
            metrics.RecordLatency(sw.ElapsedMilliseconds);
        }
    }

    public static void RecordError(string routePrefix, Stopwatch sw)
    {
        sw.Stop();
        if (RouteStats.TryGetValue(routePrefix, out var metrics))
        {
            Interlocked.Increment(ref metrics.TotalRequests);
            Interlocked.Increment(ref metrics.TotalErrors);
            metrics.RecordLatency(sw.ElapsedMilliseconds);
        }
    }

    public static Dictionary<string, object> GetSnapshot(IReadOnlyDictionary<string, RouteConfig> routes)
    {
        var routeSnapshots = new Dictionary<string, object>();

        foreach (var (prefix, config) in routes)
        {
            var metrics = RouteStats.GetOrAdd(prefix, _ => new RouteMetrics());

            routeSnapshots[prefix] = new
            {
                circuitBreaker = CircuitBreaker.GetState(prefix).ToString(),
                bulkheadAvailable = Bulkhead.GetAvailable(prefix),
                bulkheadMax = config.MaxConcurrentRequests,
                totalRequests = metrics.TotalRequests,
                totalErrors = metrics.TotalErrors,
                avgLatencyMs = metrics.GetAverageLatency()
            };
        }

        return new Dictionary<string, object>
        {
            ["uptime"] = Uptime.ToString(@"dd\.hh\:mm\:ss"),
            ["routes"] = routeSnapshots
        };
    }



    private class RouteMetrics
    {
        public int TotalRequests;
        public int TotalErrors;

        private double _total_latency;
        private int _latencyCount;
        private readonly object _lock = new();

        public void RecordLatency(double ms)
        {
            lock (_lock)
            {
                _total_latency += ms;
                _latencyCount++;
            }
        }

        public double GetAverageLatency()
        {
            lock (_lock)
            {
                return _latencyCount == 0 ? 0 : Math.Round(_total_latency / _latencyCount, 1);
            }
        }
    }
}