using System.Security.Cryptography;
using System.Text;

namespace Gateway;

public static class Auth
{
    public const string ApiKeyHeader = "X-Api-Key";

    public static bool IsAnonymousPath(string forwardPath, string[] allowAnonymousPrefixes)
    {
        foreach (var p in allowAnonymousPrefixes)
        {
            if (forwardPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool HasValidApiKey(HttpContext ctx, string expectedKey)
    {
        if (!ctx.Request.Headers.TryGetValue(ApiKeyHeader, out var provided))
            return false;

        var providedKey = provided.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedKey))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(providedKey);
        var b = Encoding.UTF8.GetBytes(expectedKey);

        if (a.Length != b.Length) return false;

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}