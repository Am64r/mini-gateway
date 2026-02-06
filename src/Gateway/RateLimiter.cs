using System.Collections.Concurrent;
using System.Data;
using System.Threading;

namespace Gateway;

public static class RateLimiter
{
    // ConcurrentDictionary: thread-safe via striped locking (one lock per bucket segment),
    // so threads hitting different keys don't block each other.
    private static readonly ConcurrentDictionary<string, WindowCounter> Counters = new();

    public static RateLimitResult Check(string clientId, string routePrefix, RouteConfig config)
    {
        var now = DateTimeOffset.UtcNow;

        // Composite key: each client gets independent limits per route
        var key = $"{routePrefix}:{clientId}";

        // AddOrUpdate: atomic upsert. Key missing → add factory. Key exists → update factory.
        // The factories are NOT called under a lock — two threads can run the update factory
        // concurrently on the same key. This is safe because both return the same object
        // reference and Interlocked.Increment works on the shared memory.
        var counter = Counters.AddOrUpdate(
            key,
            _ => new WindowCounter(now, 1),
            (_, existing) =>
            {
                var windowEnd = existing.WindowStart + config.Window;

                if (now >= windowEnd)
                {
                    // Window expired. If two threads both see this, both create a new counter
                    // and one gets discarded and we undercount by 1.
                    // race condition, but acceptable for rate limiting
                    return new WindowCounter(now, 1);
                }

                // Interlocked.Increment: atomic read-modify-write via CPU instruction (lock xadd).
                // Regular ++ is three steps (read, add, write) — two threads can both read 5,
                // both write 6, losing an increment.
                // in this case 2 threads incrementing 5 will result in 7
                var newCount = Interlocked.Increment(ref existing.Count);
                return existing;
            });

        if (counter.Count > config.RequestsPerWindow)
        {
            var retryAfter = counter.WindowStart + config.Window - now;
            return new RateLimitResult(false, retryAfter);
        }

        return new RateLimitResult(true, TimeSpan.Zero);
    }

    // Must be a class (reference type), not a struct. As a struct, AddOrUpdate would copy
    // by value — two threads would each increment their own copy and one gets thrown away.
    // As a class, both threads point to the same heap object so Interlocked hits the same memory.
    public sealed class WindowCounter
    {
        public DateTimeOffset WindowStart;
        public int Count;

        public WindowCounter(DateTimeOffset windowStart, int count)
        {
            WindowStart = windowStart;
            Count = count;
        }
    }

    public readonly record struct RateLimitResult(bool Allowed, TimeSpan retryAfter);
}


