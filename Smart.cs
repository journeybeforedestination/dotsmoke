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
