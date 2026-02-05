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
        TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("TIMEOUT_API_A_MS"), out var a) ? a : 1500)
    ),
    ["/api/b"] = new RouteConfig(
        Environment.GetEnvironmentVariable("UPSTREAM_SERVICE_B") ?? throw new Exception("UPSTREAM_SERVICE_B not set in .env"),
        TimeSpan.FromMilliseconds(int.TryParse(Environment.GetEnvironmentVariable("TIMEOUT_API_B_MS"), out var b) ? b : 1500)
    ),
};

// Catch-all route captures every request and forwards to Proxy.HandleAsync
app.Map("/{**catchall}", (HttpContext ctx, IHttpClientFactory factory) =>
    Proxy.HandleAsync(ctx, factory, routes));

app.Run();

