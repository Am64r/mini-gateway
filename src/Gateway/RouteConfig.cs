namespace Gateway;

public sealed record RouteConfig(
    string UpstreamBaseUrl,
    TimeSpan Timeout,
    string[] AllowAnonymousPrefixes);