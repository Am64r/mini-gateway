namespace Gateway;

public static class Retry
{
    // safe methods are idempotent, repeating them has the same effect as the first request
    public static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS"
    };

    public static bool IsSafeHttpMethod(string method) => SafeMethods.Contains(method);

    public static bool ShouldRetry(HttpResponseMessage? response, Exception? e)
    {
        if (e is HttpRequestException or OperationCanceledException)
            return true;

        // 5xx errors are server errors, so we should retry
        if (response is not null && (int)response.StatusCode >= 500)
        {
            return true;
        }

        return false;
    }

}