var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var logger = app.Logger;

app.Use(async (ctx, next) =>
{
    var corr = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    logger.LogInformation("ServiceB received {Path} corr={CorrelationId}", ctx.Request.Path.Value, corr ?? "none");

    await next();
});


const string ServiceName = "service-b";

app.MapGet("/", () => Results.Ok(new { service = ServiceName, message = "ok" }));

app.MapGet("/health", () => Results.Ok(new { service = ServiceName, message = "healthy" }));

app.MapGet("/ping", () => Results.Ok(new { service = ServiceName, message = "pong" }));

app.MapGet("/slow", async (int ms = 500) =>
{
    ms = Math.Clamp(ms, 0, 30_000);
    await Task.Delay(ms);
    return Results.Ok(new { service = ServiceName, ms });
});

app.MapGet("/fail", (double rate = 0.5) =>
{
    rate = Math.Clamp(rate, 0.0, 1.0);
    return Random.Shared.NextDouble() < rate
    ? Results.StatusCode(500)
    : Results.Ok(new { service = ServiceName, ok = true, rate });
});


app.Run();


