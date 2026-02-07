namespace Gateway;

public static class Bulkhead
{
    // One semaphore per route. Plain Dictionary (not ConcurrentDictionary) because
    // it's populated once at startup and never modified — no concurrent writes.
    private static readonly Dictionary<string, SemaphoreSlim> Semaphores = new(StringComparer.OrdinalIgnoreCase);

    // Called once at startup before any requests arrive.
    // SemaphoreSlim(initial, max): starts with N slots available, can never exceed N.
    public static void Init(IReadOnlyDictionary<string, RouteConfig> routes)
    {
        foreach (var (prefix, config) in routes)
        {
            Semaphores[prefix] = new SemaphoreSlim(config.MaxConcurrentRequests, config.MaxConcurrentRequests);
        }
    }

    // Try to grab a slot. TimeSpan.Zero means don't wait — return false immediately
    // if no slots available. We want fast rejection, not queuing.
    public static async Task<bool> TryAcquire(string routePrefix)
    {
        if (!Semaphores.TryGetValue(routePrefix, out var semaphore))
            return true;

        return await semaphore.WaitAsync(TimeSpan.Zero);
    }

    // Return the slot. Called in a finally block so it always runs — even if the
    // upstream times out or errors. Without this, failed requests would permanently
    // reduce capacity.
    public static void Release(string routePrefix)
    {
        if (Semaphores.TryGetValue(routePrefix, out var semaphore))
            semaphore.Release();
    }
}
