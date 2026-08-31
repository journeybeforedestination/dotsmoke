using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// Mints id_tokens the way an EHR would, so the validation rules can be exercised without
/// one. The JWKS is hand-built rather than serialized from a library type, because it is
/// standing in for a document published by someone else.
/// </summary>
internal static class TestIdTokens
{
    public const string Issuer = "https://ehr.example/r4/fhir";
    public const string Audience = "smart-on-fhir-demo";
    public const string FhirUser = "Practitioner/prac-1";

    /// <summary>A fixed clock, so "expired" means the same thing on every machine and day.</summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    public static TimeProvider Clock { get; } = new FixedClock(Now);

    /// <summary>The key the EHR signs with, and one it does not.</summary>
    public static readonly RSA Ehr = RSA.Create(2048);
    public static readonly RSA Impostor = RSA.Create(2048);

    public static IList<SecurityKey> Published(params RSA[] keys) =>
        [.. keys.Select(SecurityKey (key) => new RsaSecurityKey(key))];

    /// <summary>
    /// The document an EHR serves at its jwks_uri. No <c>kid</c>, matching what the SMART
    /// App Launcher actually publishes against tokens whose headers carry none either.
    /// </summary>
    public static string JwksJson(params RSA[] keys) =>
        $$"""
        {"keys":[{{string.Join(
            ",",
            keys.Select(key =>
            {
                var p = key.ExportParameters(includePrivateParameters: false);
                return $$"""
                    {"kty":"RSA","alg":"RS256","key_ops":["verify"],"n":"{{Base64UrlEncoder.Encode(
                        p.Modulus!
                    )}}","e":"{{Base64UrlEncoder.Encode(p.Exponent!)}}"}
                    """;
            })
        )}}]}
        """;

    public static string Token(
        RSA? signedWith = null,
        string issuer = Issuer,
        string audience = Audience,
        string? fhirUser = FhirUser,
        DateTimeOffset? expires = null
    )
    {
        var claims = new Dictionary<string, object> { ["sub"] = "user-1" };
        if (fhirUser is not null)
            claims["fhirUser"] = fhirUser;

        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = claims,
                IssuedAt = Now.UtcDateTime,
                NotBefore = Now.UtcDateTime,
                Expires = (expires ?? Now.AddHours(1)).UtcDateTime,

                // No KeyId on the key, so no 'kid' in the header — the case the launcher produces.
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(signedWith ?? Ehr),
                    SecurityAlgorithms.RsaSha256
                ),
            }
        );
    }
}
