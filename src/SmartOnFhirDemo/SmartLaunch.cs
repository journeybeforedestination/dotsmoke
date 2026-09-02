using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Options;

namespace SmartOnFhirDemo;

/// <summary>
/// How the launch step ended. The private constructor closes the hierarchy: only the
/// cases below can exist, so a caller that handles them all has handled everything.
/// </summary>
public abstract record LaunchOutcome
{
    private LaunchOutcome() { }

    /// <summary>
    /// Ready to hand the browser to the EHR. The session must outlive the redirect.
    /// <paramref name="WellKnownUrl"/> and <paramref name="ConfigurationJson"/> record
    /// where discovery looked and what it got; nothing in either is secret, and the
    /// plain launch simply ignores them.
    /// </summary>
    public sealed record Prepared(
        string AuthorizeUrl,
        string State,
        LaunchState Session,
        string WellKnownUrl,
        string ConfigurationJson
    ) : LaunchOutcome;

    public sealed record MissingParameters : LaunchOutcome;

    /// <summary>The launch named an EHR this app is not registered with.</summary>
    public sealed record UntrustedIssuer(string Iss) : LaunchOutcome;

    public sealed record DiscoveryFailed(string WellKnown, string Reason) : LaunchOutcome;
}

/// <summary>How the callback step ended.</summary>
public abstract record CallbackOutcome
{
    private CallbackOutcome() { }

    /// <summary>
    /// The launch finished. <paramref name="TokenJson"/> is the token response with the
    /// credentials already removed, and <paramref name="Token"/> is everything it said
    /// apart from them — the access token itself is not on this record at all, so no
    /// caller can render or store it.
    /// </summary>
    /// <param name="Identity">
    /// What the id_token claimed, once validated, or null when there was none to validate
    /// or it did not survive validation. Identity is supplementary here: the app's job is
    /// the patient summary, and none of the ways it can be missing stop the launch.
    /// </param>
    /// <param name="IdentityUnavailable">Why <paramref name="Identity"/> is null, as a sentence.</param>
    /// <param name="User">
    /// The resource <c>fhirUser</c> named, read back from the EHR. Null whenever the claim
    /// was absent, could not be followed, or the server would not return it — the claim in
    /// <paramref name="Identity"/> can be perfectly good while this is not.
    /// </param>
    /// <param name="UserUnavailable">Why <paramref name="User"/> is null, as a sentence.</param>
    public sealed record Completed(
        PatientSummary Summary,
        string RawJson,
        TokenFacts Token,
        string TokenJson,
        string PatientUrl,
        IdTokenFacts? Identity = null,
        string? IdentityUnavailable = null,
        LaunchUser? User = null,
        string? UserUnavailable = null
    ) : CallbackOutcome;

    public sealed record MissingParameters : CallbackOutcome;

    /// <summary>The EHR sent the user back with an error instead of a code.</summary>
    public sealed record AuthorizationDenied(string Reason) : CallbackOutcome;

    /// <summary>No launch is in flight for this state — expired, already used, or never issued.</summary>
    public sealed record UnknownLaunch : CallbackOutcome;

    public sealed record TokenExchangeFailed(int Status, string Reason) : CallbackOutcome;

    /// <summary>
    /// The token endpoint did not answer at all — refused, unreachable, or slower than the
    /// client's timeout. Kept apart from <see cref="TokenExchangeFailed"/> because there is
    /// no status to report: nothing was answered.
    /// </summary>
    public sealed record TokenEndpointUnreachable(string Reason) : CallbackOutcome;

    public sealed record NoAccessToken : CallbackOutcome;

    public sealed record NoPatientContext : CallbackOutcome;

    public sealed record PatientNotFound(string Iss, string PatientId) : CallbackOutcome;

    public sealed record PatientReadFailed(int Status, string Reason) : CallbackOutcome;

    public sealed record IncompatibleFhirVersion(string Iss, string Reason) : CallbackOutcome;
}

/// <summary>
/// How the callback step ended, and — only when it ended in a launch — the live context
/// that launch established.
///
/// The context is a second return rather than a field on
/// <see cref="CallbackOutcome.Completed"/> so that the outcome stays credential-free. The
/// narrated launch caches and renders that outcome; a token on it would be a token in the
/// cache under a transcript, and on a page.
/// </summary>
public sealed record CallbackResult(CallbackOutcome Outcome, LaunchContext? Context = null)
{
    /// <summary>Every way a callback ends short of a launch, which is most of them.</summary>
    public static implicit operator CallbackResult(CallbackOutcome outcome) => new(outcome);

    /// <summary>The named alternative to the conversion above.</summary>
    public static CallbackResult From(CallbackOutcome outcome) => new(outcome);
}

/// <summary>
/// The SMART EHR launch itself: discovery and the authorization request on the way
/// out, the token exchange and patient read on the way back. It knows nothing about
/// ASP.NET — callers decide how to present each outcome, and where to keep the launch
/// in flight between the two steps.
/// </summary>
public sealed partial class SmartLaunch(
    IHttpClientFactory clients,
    FhirClients fhirClients,
    IOptions<SmartOptions> options,
    Jwks jwks,
    TimeProvider clock,
    ILogger<SmartLaunch> log
)
{
    private SmartOptions Options => options.Value;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refused a launch from untrusted issuer {iss}"
    )]
    private partial void LogUntrustedIssuer(string iss);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMART discovery failed for {wellKnown}")]
    private partial void LogDiscoveryFailed(Exception ex, string wellKnown);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The token endpoint {tokenEndpoint} did not answer"
    )]
    private partial void LogTokenEndpointUnreachable(Exception ex, string tokenEndpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading Patient/{patientId} failed")]
    private partial void LogPatientReadFailed(Exception ex, string? patientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Reading the launching user {reference} failed"
    )]
    private partial void LogUserReadFailed(Exception ex, string reference);

    /// <summary>Discover the EHR's OAuth endpoints and build the authorization request.</summary>
    public async Task<LaunchOutcome> BeginAsync(
        string? iss,
        string? launch,
        string redirectUri,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(launch))
            return new LaunchOutcome.MissingParameters();

        // Before the issuer is contacted at all, not after.
        if (!Smart.IsTrustedIssuer(iss, Options.TrustedIssuers))
        {
            LogUntrustedIssuer(iss);
            return new LaunchOutcome.UntrustedIssuer(iss);
        }

        var wellKnown = $"{iss.TrimEnd('/')}/.well-known/smart-configuration";

        // Read the document as text and then parse it, rather than straight into the
        // two fields this app uses: the learn pages show what the EHR actually published.
        string configJson;
        SmartConfiguration? config;
        try
        {
            using var discovery = await clients.CreateClient().GetAsync(wellKnown, ct);
            discovery.EnsureSuccessStatusCode();

            configJson = await discovery.Content.ReadAsStringAsync(ct);
            config = JsonSerializer.Deserialize<SmartConfiguration>(configJson);
        }
        // TaskCanceledException among them because the clients carry a timeout: an issuer
        // that never answers is discovery failing, not an unhandled exception on a URL a
        // stranger can open.
        catch (Exception ex)
            when (ex
                    is HttpRequestException
                        or JsonException
                        or NotSupportedException
                        or TaskCanceledException
            )
        {
            LogDiscoveryFailed(ex, wellKnown);
            return new LaunchOutcome.DiscoveryFailed(wellKnown, ex.Message);
        }

        if (config is null)
            return new LaunchOutcome.DiscoveryFailed(
                wellKnown,
                "the server returned an empty configuration"
            );

        if (Elsewhere(iss, config) is { } published)
            return new LaunchOutcome.DiscoveryFailed(wellKnown, published);

        var (verifier, challenge) = Smart.NewPkce();
        var state = Smart.NewState();

        return new LaunchOutcome.Prepared(
            Smart.BuildAuthorizeUrl(config, Options, redirectUri, iss, launch, state, challenge),
            state,
            new LaunchState(
                iss,
                config.TokenEndpoint,
                verifier,
                redirectUri,
                config.Issuer,
                config.JwksUri
            ),
            wellKnown,
            configJson
        );
    }

    /// <summary>
    /// Why the endpoints this configuration published may not be used, or null if they may.
    ///
    /// The allowlist trusts an <em>origin</em>, and the path beneath it is the EHR's to
    /// choose — which is exactly what lets the SMART App Launcher encode a whole simulation
    /// into one. So whoever controls a path on a trusted host controls this document, and
    /// these two fields are what it steers: <c>authorization_endpoint</c> is where this app
    /// sends the browser, so one pointing elsewhere is an open redirect wearing this app's
    /// domain, and <c>token_endpoint</c> is where it posts the authorization code, so one
    /// pointing elsewhere is the code handed to a stranger.
    ///
    /// SMART requires neither to sit on the FHIR base's origin. This app requires it anyway,
    /// because an origin is the whole of what it checked.
    /// </summary>
    private static string? Elsewhere(string iss, SmartConfiguration config) =>
        Published(iss, "authorization_endpoint", config.AuthorizationEndpoint)
        ?? Published(iss, "token_endpoint", config.TokenEndpoint);

    private static string? Published(string iss, string name, string? endpoint) =>
        string.IsNullOrEmpty(endpoint) ? $"it publishes no {name}"
        : Smart.SameOrigin(iss, endpoint) ? null
        : $"its {name} is on another origin than the FHIR server this app trusts, and this "
            + "app follows neither an authorization request nor an authorization code off it";

    /// <summary>
    /// Trade the authorization code for an access token and read the patient it points
    /// at. The token is used once and never leaves this method.
    /// </summary>
    /// <param name="launch">The launch this callback belongs to, or null if none is in flight.</param>
    public async Task<CallbackResult> CompleteAsync(
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        LaunchState? launch,
        CancellationToken ct
    )
    {
        if (!string.IsNullOrEmpty(error))
            return new CallbackOutcome.AuthorizationDenied(errorDescription ?? error);

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return new CallbackOutcome.MissingParameters();

        if (launch is null)
            return new CallbackOutcome.UnknownLaunch();

        using var form = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = launch.RedirectUri,
                ["code_verifier"] = launch.CodeVerifier,
                ["client_id"] = Options.ClientId,
            }
        );

        var (status, body, unreachable) = await ExchangeAsync(launch.TokenEndpoint, form, ct);

        if (unreachable is { } silence)
            return new CallbackOutcome.TokenEndpointUnreachable(silence);

        if (status is < 200 or >= 300)
            return new CallbackOutcome.TokenExchangeFailed(status, body);

        TokenResponse? token;
        try
        {
            token = JsonSerializer.Deserialize<TokenResponse>(body);
        }
        catch (JsonException)
        {
            return new CallbackOutcome.NoAccessToken();
        }

        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            return new CallbackOutcome.NoAccessToken();

        if (string.IsNullOrEmpty(token.Patient))
            return new CallbackOutcome.NoPatientContext();

        // The only point at which the response body and the credentials part company.
        // Everything downstream sees the redacted copy.
        var tokenJson = Smart.Redact(body, "access_token", "refresh_token", "id_token");

        var identity = await IdentifyAsync(launch, token, ct);

        // Before the read rather than after it: the read is audited against this launch,
        // and the handler that audits it is handed the context at construction.
        var context = Established(launch, token, identity.Facts?.FhirUser);

        return await ReadPatientAsync(context, token, tokenJson, identity, ct);
    }

    /// <summary>
    /// What the token endpoint answered, or the reason it did not. Wrapped because the
    /// clients carry a timeout: an EHR that goes quiet has to land on the same page as one
    /// that answers badly, rather than as an exception out of the callback.
    /// </summary>
    private async Task<(int Status, string Body, string? Unreachable)> ExchangeAsync(
        string tokenEndpoint,
        FormUrlEncodedContent form,
        CancellationToken ct
    )
    {
        try
        {
            using var response = await clients.CreateClient().PostAsync(tokenEndpoint, form, ct);

            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogTokenEndpointUnreachable(ex, tokenEndpoint);
            return (0, "", ex.Message);
        }
    }

    /// <summary>
    /// Who started this launch, if the EHR said and the claim can be trusted. Every way
    /// this can fail returns a sentence rather than throwing: the patient summary does
    /// not depend on it, and a launch that works should not be lost to an absent name.
    /// </summary>
    private async Task<(IdTokenFacts? Facts, string? Unavailable)> IdentifyAsync(
        LaunchState launch,
        TokenResponse token,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(token.IdToken))
            return (
                null,
                "The token response carried no id_token, so the EHR did not grant the "
                    + "openid scope this app asked for."
            );

        if (string.IsNullOrEmpty(launch.Issuer) || string.IsNullOrEmpty(launch.JwksUri))
            return (
                null,
                "The EHR's SMART configuration publishes no issuer and jwks_uri, so there is "
                    + "nothing to validate the id_token against. Unvalidated claims are not shown."
            );

        // The same rule the two discovered endpoints are refused for, with a different
        // consequence: whoever answers at a jwks_uri decides which id_tokens this app
        // believes. Identity degrades rather than failing, so this is a sentence and the
        // launch goes on without a name.
        if (!Smart.SameOrigin(launch.Iss, launch.JwksUri))
            return (
                null,
                "The EHR publishes its signing keys on another origin than its FHIR server, so "
                    + "they were not fetched. This app trusts an EHR by origin, and the keys that "
                    + "decide which id_tokens it believes have to come from the one it trusts."
            );

        if (await jwks.KeysAsync(launch.JwksUri, ct) is not { Count: > 0 } keys)
            return (
                null,
                $"The signing keys at {launch.JwksUri} could not be read, so the id_token "
                    + "could not be validated."
            );

        var outcome = await IdToken.ValidateAsync(
            token.IdToken,
            keys,
            launch.Issuer,
            Options.ClientId,
            clock
        );

        return outcome switch
        {
            IdTokenOutcome.Valid(var facts) => (facts, null),
            IdTokenOutcome.Invalid(var reason) => (null, $"The id_token was refused: {reason}."),
            _ => throw new UnreachableException($"{outcome.GetType().Name} is not an outcome."),
        };
    }

    private async Task<CallbackResult> ReadPatientAsync(
        LaunchContext context,
        TokenResponse token,
        string tokenJson,
        (IdTokenFacts? Facts, string? Unavailable) identity,
        CancellationToken ct
    )
    {
        var iss = context.Iss;
        var patientUrl = $"{iss.TrimEnd('/')}/Patient/{token.Patient}";

        // The one read that asks the server what it is first: nothing is known about it
        // yet, and a launch against a server that is not R4 should say so rather than fail
        // on the first field that is missing.
        using var client = fhirClients.Open(context, verifyVersion: true);
        var fhir = client.Fhir;

        try
        {
            var patient = await fhir.ReadAsync<Patient>($"Patient/{token.Patient}", ct: ct);

            if (patient is null)
                return new CallbackOutcome.PatientNotFound(iss, token.Patient!);

            var user = await ReadUserAsync(fhir, iss, context.FhirUser, ct);

            return new CallbackResult(
                new CallbackOutcome.Completed(
                    PatientSummary.From(patient),
                    patient.ToJson(pretty: true),
                    TokenFacts.From(token),
                    tokenJson,
                    patientUrl,
                    identity.Facts,
                    identity.Unavailable,
                    user.User,
                    user.Unavailable
                ),
                context
            );
        }
        catch (FhirOperationException ex)
        {
            LogPatientReadFailed(ex, token.Patient);
            return new CallbackOutcome.PatientReadFailed(
                (int)ex.Status,
                LaunchMessages.Describe(ex.Outcome) ?? ex.Message
            );
        }
        catch (NotSupportedException ex)
        {
            // FhirClientSettings.VerifyFhirVersion throws when the server is not R4.
            return new CallbackOutcome.IncompatibleFhirVersion(iss, ex.Message);
        }
    }

    /// <summary>
    /// The launch, as everything after the callback will need it. This is where the access
    /// token stops being a local and starts being state, so it is also where the app takes
    /// on the obligation to say when it stops being good for anything.
    /// </summary>
    private LaunchContext Established(LaunchState launch, TokenResponse token, string? fhirUser) =>
        new(
            Smart.NewOpaqueId(),
            launch.Iss,
            // Non-null: a launch that reached here passed the trust check, which refuses
            // anything that is not an absolute http(s) URL.
            Smart.Origin(launch.Iss)!,
            token.Patient!,
            fhirUser,
            // An EHR that does not say falls back to the cache's own five minutes, which
            // is short enough to be honest about how little the app was told.
            clock.GetUtcNow()
                + (
                    token.ExpiresIn is { } seconds
                        ? TimeSpan.FromSeconds(seconds)
                        : LaunchCache.Lifetime
                ),
            token.AccessToken
        );

    /// <summary>
    /// Reads whoever <c>fhirUser</c> named, on the same authenticated client the patient
    /// was read with. Every failure is a sentence, not an exception: a launch that read its
    /// patient has done its job whether or not it can also put a name to the clinician.
    /// </summary>
    private async Task<(LaunchUser? User, string? Unavailable)> ReadUserAsync(
        FhirClient fhir,
        string iss,
        string? fhirUser,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(fhirUser))
            return (null, null);

        var (location, refused) = Location(iss, fhirUser);

        if (location is null)
            return (null, refused);

        try
        {
            var resource = await fhir.ReadAsync<Resource>(location, ct: ct);

            return resource is null
                ? (null, $"The EHR returned nothing for {fhirUser}.")
                : (LaunchUser.From(resource), null);
        }
        catch (FhirOperationException ex)
        {
            // Commonly a 403: asking for user/Practitioner.read does not oblige an EHR
            // to grant it, and an app is expected to cope with getting less than it asked.
            // Not against the SMART App Launcher, though — it does not enforce user/ scopes
            // at all, so this read succeeds there whether or not the scope was granted, and
            // a launch against it proves less about scopes than it appears to.
            LogUserReadFailed(ex, fhirUser);
            return (null, $"The EHR would not return {fhirUser} ({(int)ex.Status}).");
        }
    }

    /// <summary>
    /// Where to read the launching user from, or the reason the reference will not be
    /// followed.
    ///
    /// SMART says fhirUser SHOULD be an absolute URL; the SMART App Launcher returns a
    /// relative one, so both are handled. An absolute reference to a different origin is
    /// refused rather than followed, because following it would send this server's access
    /// token to a server the token was never issued for.
    ///
    /// Which makes "is this absolute?" the load-bearing question, and
    /// <c>Uri.IsWellFormedUriString</c> the wrong way to ask it: it answers false for
    /// <c>//elsewhere.example/Practitioner/1</c> and for any absolute URL carrying a
    /// character it would have to escape, and both of those resolve against the FHIR base to
    /// a host that is not it. Every reference that is not absolute therefore has to look
    /// like a reference — <c>ResourceType/id</c>, the only shape a relative one may take —
    /// rather than merely fail to look absolute.
    /// </summary>
    private static (string? Location, string? Refused) Location(string iss, string fhirUser) =>
        Uri.TryCreate(fhirUser, UriKind.Absolute, out _) ? Absolute(iss, fhirUser)
        : Reference().IsMatch(fhirUser) ? (fhirUser, null)
        : (
            null,
            "The id_token's fhirUser is not a reference this app can place: neither an "
                + "absolute URL nor a plain ResourceType/id. One it cannot place is one it "
                + "cannot prove stays on this launch's server, so it was not followed."
        );

    private static (string? Location, string? Refused) Absolute(string iss, string fhirUser) =>
        Smart.SameOrigin(iss, fhirUser)
            ? (fhirUser, null)
            : (
                null,
                "The id_token points at a FHIR server other than the one this launch is "
                    + "for, so it was not followed — the access token belongs to this server alone."
            );

    /// <summary>
    /// A relative FHIR reference: a resource type, and an id as FHIR itself defines one.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z]+/[A-Za-z0-9\-.]{1,64}$")]
    private static partial Regex Reference();
}
