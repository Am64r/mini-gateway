namespace Gateway;

// Closed = healthy, requests flow through
// Open = upstream is broken, reject immediately with 503
// HalfOpen = cooldown expired, allow one test request through
public enum State { Closed, Open, HalfOpen }

public static class CircuitBreaker
{
    // One circuit per route, populated at startup and never modified.
    private static readonly Dictionary<string, CircuitState> Circuits = new(StringComparer.OrdinalIgnoreCase);

    // Single lock for all state transitions. We need exclusive access because
    // transitions read and write multiple fields atomically (State, FailureCount, OpenedAt).
    // A per-circuit lock would scale better, but with a handful of routes this is fine.
    private static readonly object Lock = new();

    public static void Init(IReadOnlyDictionary<string, RouteConfig> routes)
    {
        foreach (var (prefix, config) in routes)
        {
            Circuits[prefix] = new CircuitState(config.CircuitBreakerThreshold, config.CircuitBreakerCooldown);
        }
    }

    public static bool AllowRequest(string routePrefix)
    {
        if (!Circuits.TryGetValue(routePrefix, out var circuit))
            return true;

        lock (Lock)
        {
            return circuit.State switch
            {
                State.Closed => true,
                State.Open => CheckCooldown(circuit),
                // HalfOpen: one test request is already in flight, reject others
                State.HalfOpen => false,
                _ => true
            };
        }
    }

    // Called after a successful upstream response
    public static void RecordSuccess(string routePrefix)
    {
        if (!Circuits.TryGetValue(routePrefix, out var circuit))
            return;

        lock (Lock)
        {
            circuit.FailureCount = 0;

            // If we were half-open, the test request succeeded — upstream recovered
            if (circuit.State == State.HalfOpen)
            {
                circuit.State = State.Closed;
            }
        }
    }

    // Called after a failed upstream request (5xx, timeout, connection error)
    public static void RecordFailure(string routePrefix)
    {
        if (!Circuits.TryGetValue(routePrefix, out var circuit))
            return;

        lock (Lock)
        {
            circuit.FailureCount++;

            if (circuit.State == State.HalfOpen)
            {
                // Test request failed — upstream still broken, reopen and restart cooldown
                circuit.State = State.Open;
                circuit.OpenedAt = DateTimeOffset.UtcNow;
            }
            else if (circuit.State == State.Closed && circuit.FailureCount >= circuit.Threshold)
            {
                // Too many consecutive failures — trip the circuit
                circuit.State = State.Open;
                circuit.OpenedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    // Called when circuit is Open — check if cooldown has expired
    private static bool CheckCooldown(CircuitState circuit)
    {
        var elapsed = DateTimeOffset.UtcNow - circuit.OpenedAt;

        if (elapsed >= circuit.Cooldown)
        {
            // Cooldown expired — transition to half-open, allow one test request
            circuit.State = State.HalfOpen;
            return true;
        }

        // Still in cooldown, reject
        return false;
    }

    public static State GetState(string routePrefix)
    {
        if (Circuits.TryGetValue(routePrefix, out var circuit))
            return circuit.State;

        return State.Closed;
    }

    private class CircuitState
    {
        public State State = State.Closed;
        public int FailureCount = 0;
        public DateTimeOffset OpenedAt;
        public int Threshold;
        public TimeSpan Cooldown;

        public CircuitState(int threshold, TimeSpan cooldown)
        {
            Threshold = threshold;
            Cooldown = cooldown;
        }
    }
}
