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

    public static async Task HandleAsync(
        HttpContext ctx,
        IHttpClientFactory factory,
        IReadOnlyDictionary<string, string> routes)
    {
        // 1. Match route: find upstream for this path prefix
        var requestPath = ctx.Request.Path.Value ?? "/";
        if (!TryMatch(requestPath, routes, out var upstreamBase, out var forwardPath))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("No route matched.");
            return;
        }

        // 2. Build upstream URI: /api/a/ping?x=1 → http://localhost:5051/ping?x=1
        var upstreamUri = BuildUpstreamUri(upstreamBase, forwardPath, ctx.Request.QueryString.Value);

        // 3. Create upstream request with method, filtered headers, body stream
        using var upstreamRequest = CreateUpstreamRequest(ctx.Request, upstreamUri);

        // 4. Send request
        // - ResponseHeadersRead: stream body instead of buffering entire response
        // - ctx.RequestAborted: cancel upstream call if client disconnects
        var client = factory.CreateClient();
        using var upstreamResponse = await client.SendAsync(
            upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ctx.RequestAborted);

        // 5. Stream response back to client
        await CopyDownstreamResponse(ctx, upstreamResponse);
    }

    private static Uri BuildUpstreamUri(string baseUrl, string path, string? query)
        => new Uri($"{baseUrl}{path}{query ?? ""}");

    private static HttpRequestMessage CreateUpstreamRequest(HttpRequest incoming, Uri upstreamUri)
    {
        var req = new HttpRequestMessage(new HttpMethod(incoming.Method), upstreamUri);

        // Attach body as stream (no buffering) if present
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

            // Try request headers first, fall back to content headers
            if (!to.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray()))
                to.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        }
    }

    private static async Task CopyDownstreamResponse(HttpContext ctx, HttpResponseMessage upstreamResponse)
    {
        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;

        // Copy response + content headers (excluding hop-by-hop)
        foreach (var h in upstreamResponse.Headers)
            if (!HopByHop.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in upstreamResponse.Content.Headers)
            if (!HopByHop.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();

        // Stream body to client; cancel if client disconnects
        await upstreamResponse.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    // Finds longest matching route prefix. Returns upstream base URL and remaining path.
    private static bool TryMatch(
        string requestPath,
        IReadOnlyDictionary<string, string> routes,
        out string upstreamBase,
        out string forwardPath)
    {
        upstreamBase = "";
        forwardPath = "";
        string? match = null;

        // Longest prefix wins: /api/a/ping matches "/api/a" over "/api"
        foreach (var prefix in routes.Keys)
        {
            if (requestPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                if (match is null || prefix.Length > match.Length)
                    match = prefix;
        }

        if (match is null) return false;

        upstreamBase = routes[match];
        var rest = requestPath.Substring(match.Length);
        forwardPath = string.IsNullOrEmpty(rest) ? "/" : rest.StartsWith("/") ? rest : "/" + rest;
        return true;
    }
}