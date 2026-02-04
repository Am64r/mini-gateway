var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

const string ServiceName = "service-b";

app.MapGet("/", () => Results.Ok(new { service = ServiceName, message = "ok" }));


app.MapGet("/ping", () => Results.Ok("pong"));

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


