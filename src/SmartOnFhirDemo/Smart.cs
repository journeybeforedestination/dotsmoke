using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace SmartOnFhirDemo;

/// <summary>App registration details. Bound from the "Smart" section of appsettings.json.</summary>
public sealed class SmartOptions
{
    public string ClientId { get; set; } = "smart-on-fhir-demo";
    public string Scopes { get; set; } = "launch patient/Patient.read";

    /// <summary>
    /// The EHRs this app will accept a launch from. A real app is registered with each
    /// EHR it serves, so it knows this list up front. An empty list trusts nobody.
    /// </summary>
    public string[] TrustedIssuers { get; set; } = ["https://launch.smarthealthit.org"];
}

/// <summary>The subset of <c>.well-known/smart-configuration</c> this app needs.</summary>
public sealed record SmartConfiguration(
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

/// <summary>What must survive the round trip through the EHR, keyed by the OAuth <c>state</c>.</summary>
public sealed record LaunchState(string Iss, string TokenEndpoint, string CodeVerifier, string RedirectUri);

/// <summary>The subset of the token response this app needs.</summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("patient")] string? Patient);

public static class Smart
{
    /// <summary>
    /// Whether a launch may be accepted from this issuer. <c>iss</c> arrives as a query
    /// parameter, and everything downstream trusts it: the app fetches that host's
    /// configuration, sends the user to the authorization endpoint it names, and posts
    /// the authorization code to its token endpoint. An unchecked <c>iss</c> is therefore
    /// a server-side request forgery, an open redirect, and a way to harvest codes.
    ///
    /// Compared by origin — scheme, host and port — because a SMART issuer legitimately
    /// carries a path, and the launcher encodes launch settings into it.
    /// </summary>
    public static bool IsTrustedIssuer(string? iss, IEnumerable<string> trustedIssuers) =>
        Origin(iss) is { } origin
        && trustedIssuers.Any(trusted =>
            Origin(trusted) is { } candidate
            && string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase));

    /// <summary>Scheme, host and port of an absolute http(s) URL, or null if it is neither.</summary>
    private static string? Origin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}"
            : null;

    /// <summary>Cache key for a launch in flight.</summary>
    public static string CacheKey(string state) => $"launch:{state}";

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>PKCE (RFC 7636) verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string BuildAuthorizeUrl(
        SmartConfiguration config,
        SmartOptions options,
        string redirectUri,
        string iss,
        string launch,
        string state,
        string codeChallenge) =>
        QueryHelpers.AddQueryString(config.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["launch"] = launch,
            ["scope"] = options.Scopes,
            ["state"] = state,
            ["aud"] = iss,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });

    private static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);
}
