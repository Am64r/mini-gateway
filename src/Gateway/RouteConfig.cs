namespace Gateway;

public sealed record RouteConfig(
    string UpstreamBaseUrl,
    TimeSpan Timeout,
    string[] AllowAnonymousPrefixes,
    int RequestsPerWindow,
    TimeSpan Window,
    int MaxConcurrentRequests,
    int MaxRetries,
    TimeSpan RetryDelay);