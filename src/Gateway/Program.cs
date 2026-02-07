using Gateway;

// Load .env file from repo root (two levels up from bin/Debug/net10.0)
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// HttpClientFactory pools connections and rotates handlers for DNS refresh
builder.Services.AddHttpClient();

var app = builder.Build();

// Route table: path prefix → upstream URL (from env vars, with fallback defaults)
var routes = new Dictionary<string, RouteConfig>(StringComparer.OrdinalIgnoreCase)
{
    ["/api/a"] = new RouteConfig(
        Environment.GetEnvironmentVariable("UPSTREAM_SERVICE_A") ?? throw new Exception("UPSTREAM_SERVICE_A not set in .env"),
        TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("TIMEOUT_API_A_MS"), out var a) ? a : 1500),
        AllowAnonymousPrefixes: ["/health"],
        RequestsPerWindow: int.TryParse(Environment.GetEnvironmentVariable("RATE_LIMIT_API_A"), out var rla) ? rla : throw new Exception("RATE_LIMIT_API_A not set in .env"),
        Window: TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("RATE_WINDOW_API_A_MS"), out var rwa) ? rwa : throw new Exception("RATE_WINDOW_API_A_MS not set in .env")),
        MaxConcurrentRequests: int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS_API_A"), out var mcra) ? mcra : throw new Exception("MAX_CONCURRENT_REQUESTS_API_A not set in .env")
    ),
    ["/api/b"] = new RouteConfig(
        Environment.GetEnvironmentVariable("UPSTREAM_SERVICE_B") ?? throw new Exception("UPSTREAM_SERVICE_B not set in .env"),
        TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("TIMEOUT_API_B_MS"), out var b) ? b : 1500),
        AllowAnonymousPrefixes: ["/health"],
        RequestsPerWindow: int.TryParse(Environment.GetEnvironmentVariable("RATE_LIMIT_API_B"), out var rlb) ? rlb : throw new Exception("RATE_LIMIT_API_B not set in .env"),
        Window: TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("RATE_WINDOW_API_B_MS"), out var rwb) ? rwb : throw new Exception("RATE_WINDOW_API_B_MS not set in .env")),
        MaxConcurrentRequests: int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS_API_B"), out var mcrb) ? mcrb : throw new Exception("MAX_CONCURRENT_REQUESTS_API_B not set in .env")
    ),
};

Bulkhead.Init(routes);

// Catch-all route captures every request and forwards to Proxy.HandleAsync
app.Map("/{**catchall}", (HttpContext ctx, IHttpClientFactory factory) =>
    Proxy.HandleAsync(ctx, factory, routes));

app.Run();

