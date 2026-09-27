using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace MiniOcr.Services;

/// <summary>Shared-token auth for cluster endpoints. Header <c>Authorization: Bearer</c> or <c>X-MiniOcr-Token</c>.</summary>
public static class ClusterAuth
{
    public const string HeaderName = "X-MiniOcr-Token";

    public static bool IsAuthorized(HttpRequest request, string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
            return false;

        string? provided = null;
        if (request.Headers.TryGetValue(HeaderName, out StringValues header) &&
            !string.IsNullOrWhiteSpace(header.ToString()))
        {
            provided = header.ToString().Trim();
        }

        if (string.IsNullOrEmpty(provided) &&
            request.Headers.TryGetValue("Authorization", out StringValues auth))
        {
            string raw = auth.ToString().Trim();
            const string prefix = "Bearer ";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                provided = raw[prefix.Length..].Trim();
        }

        return FixedEquals(provided, expectedToken);
    }

    /// <summary>Length-independent compare (SHA-256 both sides, then fixed-time equals).</summary>
    public static bool FixedEquals(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(expected))
            return false;
        byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(provided ?? ""));
        byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
