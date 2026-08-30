using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SmartOnFhirDemo;

/// <summary>
/// What the id_token said, once it has been proved genuine. The token itself is not a
/// field here: like the access token, it is a credential, and a page cannot leak what it
/// was never handed.
/// </summary>
public sealed record IdTokenFacts(
    string Issuer,
    string Audience,
    string Subject,
    string? FhirUser,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt
);

/// <summary>How validation ended. Closed, in the style of <see cref="LaunchOutcome"/>.</summary>
public abstract record IdTokenOutcome
{
    private IdTokenOutcome() { }

    public sealed record Valid(IdTokenFacts Facts) : IdTokenOutcome;

    /// <summary><paramref name="Reason"/> is prose, meant to be read on a page.</summary>
    public sealed record Invalid(string Reason) : IdTokenOutcome;
}

/// <summary>
/// Proves an id_token came from the EHR that issued it, and says what it claims.
///
/// Pure: it fetches nothing and caches nothing. The caller supplies the keys, the issuer
/// to expect, the audience to expect and the clock, which is what lets every rule below
/// be tested without a network or a wait.
///
/// Worth knowing that this is optional here. OIDC Core 3.1.3.7 lets an app skip signature
/// validation when the token arrives over a direct TLS connection to the token endpoint,
/// which is exactly how it arrives in an authorization code flow. This app validates
/// anyway: the keys are one cached fetch away, and an app that only checks signatures
/// when it must is one deployment change away from not checking them when it should.
/// </summary>
public static class IdToken
{
    public static async Task<IdTokenOutcome> ValidateAsync(
        string jwt,
        IEnumerable<SecurityKey> keys,
        string issuer,
        string audience,
        TimeProvider clock
    )
    {
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKeys = keys,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,

            // The SMART App Launcher's id_tokens carry no 'kid', so there is nothing to
            // match a key on. TryAllIssuerSigningKeys defaults to true and covers this;
            // it is named here because the behaviour is load-bearing rather than incidental.
            TryAllIssuerSigningKeys = true,

            // Lifetime is checked against the clock the caller passed, not the machine's.
            // 8.22.0's TokenValidationParameters has no TimeProvider, so this is the seam.
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore <= now)
                    && expires is not null
                    && now < expires;
            },
        };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(jwt, parameters);

        if (!result.IsValid)
            return new IdTokenOutcome.Invalid(Explain(result.Exception));

        var token = (JsonWebToken)result.SecurityToken;

        return new IdTokenOutcome.Valid(
            new IdTokenFacts(
                token.Issuer,
                token.Audiences.FirstOrDefault() ?? "",
                token.Subject,
                token.TryGetClaim("fhirUser", out var fhirUser) ? fhirUser.Value : null,
                token.IssuedAt,
                token.ValidTo
            )
        );
    }

    /// <summary>
    /// A sentence rather than an IDX code. The reason is rendered on a page, and "the
    /// signature did not verify" teaches something that IDX10511 does not.
    /// </summary>
    private static string Explain(Exception? failure) =>
        failure switch
        {
            // The derived case first: with no 'kid' to match on, every published key is
            // tried, so "no key found" is what a wrong signing key actually looks like.
            SecurityTokenSignatureKeyNotFoundException =>
                "it was signed with a key the EHR does not publish",
            SecurityTokenInvalidSignatureException =>
                "its signature did not verify against any key the EHR publishes",
            SecurityTokenInvalidIssuerException =>
                "it names a different issuer than the one this launch discovered",
            SecurityTokenInvalidAudienceException =>
                "it was issued for a different app than this one",
            // One arm, not two: supplying a LifetimeValidator replaces the built-in check,
            // so a lifetime failure arrives as this rather than as Expired or NotYetValid.
            SecurityTokenInvalidLifetimeException => "it has expired, or is not valid yet",
            _ => "it could not be validated",
        };
}
