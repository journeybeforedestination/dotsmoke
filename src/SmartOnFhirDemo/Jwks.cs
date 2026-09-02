using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace SmartOnFhirDemo;

/// <summary>
/// The impure half of id_token validation: fetches the signing keys an EHR publishes at
/// its <c>jwks_uri</c>, and remembers them.
///
/// Kept apart from <see cref="IdToken"/> so the validation rules stay a pure function,
/// and apart from <see cref="SmartLaunch"/> so that class is not doing a fourth network
/// call of its own.
/// </summary>
public sealed partial class Jwks(IHttpClientFactory clients, IMemoryCache cache, ILogger<Jwks> log)
{
    /// <summary>
    /// Deliberately not <see cref="LaunchCache.Lifetime"/>. That five minutes exists
    /// because a transcript holds patient data; public signing keys hold nothing, are the
    /// same for every launch, and rotate on the order of months.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not read signing keys from {jwksUri}"
    )]
    private partial void LogFetchFailed(Exception ex, string jwksUri);

    /// <summary>The keys published at this URI, or null if they could not be read.</summary>
    public async Task<IList<SecurityKey>?> KeysAsync(string jwksUri, CancellationToken ct)
    {
        if (cache.TryGetValue($"jwks:{jwksUri}", out IList<SecurityKey>? cached))
            return cached;

        try
        {
            var json = await clients.CreateClient().GetStringAsync(jwksUri, ct);
            var keys = new JsonWebKeySet(json).GetSigningKeys();

            cache.Set(
                $"jwks:{jwksUri}",
                keys,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 }
            );
            return keys;
        }
        // TaskCanceledException because the client carries a timeout, and this is one of the
        // ways identity is allowed to be unavailable rather than fatal.
        catch (Exception ex)
            when (ex is HttpRequestException or ArgumentException or TaskCanceledException)
        {
            LogFetchFailed(ex, jwksUri);
            return null;
        }
    }
}
