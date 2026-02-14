using System.Net.Http.Headers;

namespace Gateway;

public static class Proxy
{
    // Hop-by-hop headers: describe client↔gateway connection, not end-to-end request.
    // Must NOT be forwarded to upstream.
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
    };

    const string CorrelationHeader = "X-Correlation-Id";

    public static async Task HandleAsync(
        HttpContext ctx,
        IHttpClientFactory factory,
        IReadOnlyDictionary<string, RouteConfig> routes)
    {
        var logger = ctx.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Gateway");

        // Tries to grab the CorrelationHeader if it exists
        var correlationId = ctx.Request.Headers[CorrelationHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString();
        }

        // Echo correlation ID back to client
        ctx.Response.Headers[CorrelationHeader] = correlationId;

        // 1. Match route
        var requestPath = ctx.Request.Path.Value ?? "/";
        if (!TryMatch(requestPath, routes, out var upstreamBase, out var forwardPath, out var routeTimeout, out var routeConfig, out var matchedPrefix))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("No route matched.");
            logger.LogDebug("No route matched: {Path}", requestPath);
            return;
        }

        // 2. Auth check
        var expectedKey = Environment.GetEnvironmentVariable("API_KEY");
        if (string.IsNullOrWhiteSpace(expectedKey))
            throw new Exception("API_KEY not set in .env");

        var isAnonymous = Auth.IsAnonymousPath(forwardPath, routeConfig.AllowAnonymousPrefixes);

        if (!isAnonymous && !Auth.HasValidApiKey(ctx, expectedKey))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Missing or invalid X-Api-Key");
            return;
        }

        // 3. Rate limit check
        var clientId = isAnonymous
            ? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"
            : ctx.Request.Headers[Auth.ApiKeyHeader].FirstOrDefault() ?? "unknown";

        var rateResult = RateLimiter.Check(clientId, matchedPrefix, routeConfig);
        if (!rateResult.Allowed)
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers["Retry-After"] = ((int)Math.Ceiling(rateResult.retryAfter.TotalSeconds)).ToString();
            await ctx.Response.WriteAsync("Rate limit exceeded");
            logger.LogWarning("Rate limited {ClientId} on {Path} corr={CorrelationId}",
                clientId, ctx.Request.Path.Value, correlationId);
            return;
        }

        // 4. Build upstream URI
        var upstreamUri = BuildUpstreamUri(upstreamBase, forwardPath, ctx.Request.QueryString.Value);

        if (!CircuitBreaker.AllowRequest(matchedPrefix))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("Service temporarily unavailable (circuit open)");
            logger.LogWarning("Circuit open for {Route} corr={CorrelationId}", matchedPrefix, correlationId);
            return;
        }

        // 5. Bulkhead check
        if (!await Bulkhead.TryAcquire(matchedPrefix))
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await ctx.Response.WriteAsync("Too many concurrent requests");
            logger.LogWarning("Bulkhead full for {Route} corr={CorrelationId}", matchedPrefix, correlationId);
            return;
        }

        try
        {
            // Only retry safe (idempotent) methods — POST might cause duplicate side effects
            var canRetry = Retry.IsSafeHttpMethod(ctx.Request.Method);
            var maxAttempts = canRetry ? routeConfig.MaxRetries + 1 : 1;
            var client = factory.CreateClient();

            HttpResponseMessage? upstreamResponse = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // HttpRequestMessage can only be sent once — must recreate each attempt
                using var upstreamRequest = CreateUpstreamRequest(ctx.Request, upstreamUri, correlationId);

                // Fresh timeout per attempt
                using var timeoutCts = new CancellationTokenSource(routeConfig.Timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, timeoutCts.Token);

                try
                {
                    upstreamResponse = await client.SendAsync(
                        upstreamRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        linkedCts.Token);

                    // 2xx/3xx/4xx — don't retry
                    if (!Retry.ShouldRetry(upstreamResponse, null))
                        break;

                    // 5xx — log and retry if attempts remain
                    logger.LogWarning(
                        "Upstream {Status}, attempt {Attempt}/{Max} for {Method} {Path} corr={CorrelationId}",
                        (int)upstreamResponse.StatusCode, attempt, maxAttempts,
                        ctx.Request.Method, ctx.Request.Path.Value, correlationId);

                    upstreamResponse.Dispose();
                    upstreamResponse = null;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
                {
                    if (attempt < maxAttempts)
                    {
                        logger.LogWarning(
                            "Timeout, attempt {Attempt}/{Max} for {Method} {Path} corr={CorrelationId}",
                            attempt, maxAttempts, ctx.Request.Method, ctx.Request.Path.Value, correlationId);
                    }
                    else
                    {
                        if (!ctx.Response.HasStarted)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                            await ctx.Response.WriteAsync($"Gateway timeout after {(int)routeTimeout.TotalMilliseconds}ms");
                        }
                        logger.LogWarning(
                            "Timeout proxying {Method} {Path} -> {Upstream} corr={CorrelationId}",
                            ctx.Request.Method, ctx.Request.Path.Value, upstreamUri.ToString(), correlationId);
                        CircuitBreaker.RecordFailure(matchedPrefix);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                {
                    logger.LogInformation(
                        "Client disconnected {Method} {Path} corr={CorrelationId}",
                        ctx.Request.Method, ctx.Request.Path.Value, correlationId);
                    return;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt < maxAttempts)
                    {
                        logger.LogWarning(
                            "Connection error ({Error}), attempt {Attempt}/{Max} for {Method} {Path} corr={CorrelationId}",
                            ex.Message, attempt, maxAttempts, ctx.Request.Method, ctx.Request.Path.Value, correlationId);
                    }
                    else
                    {
                        if (!ctx.Response.HasStarted)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                            await ctx.Response.WriteAsync("Upstream connection failed");
                        }
                        logger.LogError(
                            "Upstream failed {Method} {Path} corr={CorrelationId} error={Error}",
                            ctx.Request.Method, ctx.Request.Path.Value, correlationId, ex.Message);
                        CircuitBreaker.RecordFailure(matchedPrefix);
                        return;
                    }
                }

                // Exponential backoff: 100ms, 200ms, 400ms, ...
                // Jitter: add 0-50% random to prevent thundering herd
                if (attempt < maxAttempts)
                {
                    var baseDelay = routeConfig.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
                    var jitter = baseDelay * Random.Shared.NextDouble() * 0.5;
                    await Task.Delay(TimeSpan.FromMilliseconds(baseDelay + jitter));
                }
            }

            if (upstreamResponse is null)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await ctx.Response.WriteAsync("Upstream returned error after all retries");
                }
                CircuitBreaker.RecordFailure(matchedPrefix);
                return;
            }


            // Got a response — send it to client
            if (upstreamResponse is not null)
            {
                await CopyDownstreamResponse(ctx, upstreamResponse, CancellationToken.None);
                logger.LogInformation(
                    "Proxy {Method} {Path} -> {Upstream} {StatusCode} corr={CorrelationId}",
                    ctx.Request.Method, ctx.Request.Path.Value, upstreamUri.ToString(),
                    (int)upstreamResponse.StatusCode, correlationId);
                CircuitBreaker.RecordSuccess(matchedPrefix);
                upstreamResponse.Dispose();
            }
        }
        finally
        {
            Bulkhead.Release(matchedPrefix);
        }
    }

    private static Uri BuildUpstreamUri(string baseUrl, string path, string? query)
        => new Uri($"{baseUrl}{path}{query ?? ""}");

    private static HttpRequestMessage CreateUpstreamRequest(HttpRequest incoming, Uri upstreamUri, string correlationId)
    {
        var req = new HttpRequestMessage(new HttpMethod(incoming.Method), upstreamUri);
        req.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        if (incoming.ContentLength > 0 || incoming.Headers.ContainsKey("Transfer-Encoding"))
        {
            req.Content = new StreamContent(incoming.Body);
            if (!string.IsNullOrEmpty(incoming.ContentType))
                req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(incoming.ContentType);
        }

        CopyRequestHeaders(incoming.Headers, req);
        return req;
    }

    private static void CopyRequestHeaders(IHeaderDictionary from, HttpRequestMessage to)
    {
        foreach (var h in from)
        {
            if (HopByHop.Contains(h.Key)) continue;
            if (h.Key.Equals(CorrelationHeader, StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Key.Equals(Auth.ApiKeyHeader, StringComparison.OrdinalIgnoreCase)) continue;

            if (!to.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray()))
                to.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        }
    }

    private static async Task CopyDownstreamResponse(HttpContext ctx, HttpResponseMessage upstreamResponse, CancellationToken token)
    {
        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;

        foreach (var h in upstreamResponse.Headers)
            if (!HopByHop.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in upstreamResponse.Content.Headers)
            if (!HopByHop.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();

        await upstreamResponse.Content.CopyToAsync(ctx.Response.Body, token);
    }

    private static bool TryMatch(
        string requestPath,
        IReadOnlyDictionary<string, RouteConfig> routes,
        out string upstreamBase,
        out string forwardPath,
        out TimeSpan routeTimeout,
        out RouteConfig routeConfig,
        out string matchedPrefix)
    {
        upstreamBase = "";
        forwardPath = "";
        routeTimeout = default;
        routeConfig = default!;
        matchedPrefix = "";
        string? match = null;

        foreach (var prefix in routes.Keys)
        {
            if (requestPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                if (match is null || prefix.Length > match.Length)
                    match = prefix;
        }

        if (match is null) return false;

        routeConfig = routes[match];
        upstreamBase = routeConfig.UpstreamBaseUrl;
        routeTimeout = routeConfig.Timeout;
        matchedPrefix = match;

        var rest = requestPath.Substring(match.Length);
        forwardPath = string.IsNullOrEmpty(rest) ? "/" : rest.StartsWith("/") ? rest : "/" + rest;
        return true;
    }
}
