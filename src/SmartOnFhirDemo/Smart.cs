using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [property: JsonPropertyName("patient")] string? Patient)
{
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("encounter")] public string? Encounter { get; init; }
}

/// <summary>
/// Everything the token response said except the credential itself. Projecting into
/// this at the edge is what makes the access token unavailable further in: a page
/// cannot leak what it was never handed.
/// </summary>
public sealed record TokenFacts(
    string? TokenType, int? ExpiresIn, string? Scope, string? Patient, string? Encounter)
{
    public static TokenFacts From(TokenResponse token) =>
        new(token.TokenType, token.ExpiresIn, token.Scope, token.Patient, token.Encounter);
}

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

    /// <summary>
    /// Cache key for a finished launch the learn pages are still walking through. A
    /// separate namespace from <see cref="CacheKey"/>, which the same state already
    /// occupies while the launch is in flight.
    /// </summary>
    public static string TranscriptKey(string state) => $"transcript:{state}";

    /// <summary>Stands in for a value that is deliberately not shown.</summary>
    public const string Withheld = "(withheld)";

    /// <summary>
    /// Replaces the named values in a JSON object with <see cref="Withheld"/>. Anything
    /// that cannot be parsed is discarded rather than passed through, because the reason
    /// to call this is that the document holds a credential.
    /// </summary>
    public static string Redact(string json, params string[] keys)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject document)
                return Withheld;

            foreach (var key in keys.Where(document.ContainsKey))
                document[key] = Withheld;

            return document.ToJsonString();
        }
        catch (JsonException)
        {
            return Withheld;
        }
    }

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
