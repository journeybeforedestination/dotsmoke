using System.Net.Http.Headers;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
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
    public sealed record Completed(
        PatientSummary Summary,
        string RawJson,
        TokenFacts Token,
        string TokenJson,
        string PatientUrl
    ) : CallbackOutcome;

    public sealed record MissingParameters : CallbackOutcome;

    /// <summary>The EHR sent the user back with an error instead of a code.</summary>
    public sealed record AuthorizationDenied(string Reason) : CallbackOutcome;

    /// <summary>No launch is in flight for this state — expired, already used, or never issued.</summary>
    public sealed record UnknownLaunch : CallbackOutcome;

    public sealed record TokenExchangeFailed(int Status, string Reason) : CallbackOutcome;

    public sealed record NoAccessToken : CallbackOutcome;

    public sealed record NoPatientContext : CallbackOutcome;

    public sealed record PatientNotFound(string Iss, string PatientId) : CallbackOutcome;

    public sealed record PatientReadFailed(int Status, string Reason) : CallbackOutcome;

    public sealed record IncompatibleFhirVersion(string Iss, string Reason) : CallbackOutcome;
}

/// <summary>
/// The SMART EHR launch itself: discovery and the authorization request on the way
/// out, the token exchange and patient read on the way back. It knows nothing about
/// ASP.NET — callers decide how to present each outcome, and where to keep the launch
/// in flight between the two steps.
/// </summary>
public sealed partial class SmartLaunch(
    IHttpClientFactory clients,
    IOptions<SmartOptions> options,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading Patient/{patientId} failed")]
    private partial void LogPatientReadFailed(Exception ex, string? patientId);

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
        catch (Exception ex)
            when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            LogDiscoveryFailed(ex, wellKnown);
            return new LaunchOutcome.DiscoveryFailed(wellKnown, ex.Message);
        }

        if (config is null)
            return new LaunchOutcome.DiscoveryFailed(
                wellKnown,
                "the server returned an empty configuration"
            );

        var (verifier, challenge) = Smart.NewPkce();
        var state = Smart.NewState();

        return new LaunchOutcome.Prepared(
            Smart.BuildAuthorizeUrl(config, Options, redirectUri, iss, launch, state, challenge),
            state,
            new LaunchState(iss, config.TokenEndpoint, verifier, redirectUri),
            wellKnown,
            configJson
        );
    }

    /// <summary>
    /// Trade the authorization code for an access token and read the patient it points
    /// at. The token is used once and never leaves this method.
    /// </summary>
    /// <param name="launch">The launch this callback belongs to, or null if none is in flight.</param>
    public async Task<CallbackOutcome> CompleteAsync(
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

        using var response = await clients.CreateClient().PostAsync(launch.TokenEndpoint, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return new CallbackOutcome.TokenExchangeFailed((int)response.StatusCode, body);

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

        return await ReadPatientAsync(launch.Iss, token, tokenJson, ct);
    }

    private async Task<CallbackOutcome> ReadPatientAsync(
        string iss,
        TokenResponse token,
        string tokenJson,
        CancellationToken ct
    )
    {
        var patientUrl = $"{iss.TrimEnd('/')}/Patient/{token.Patient}";

        var http = clients.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token.AccessToken
        );

        using var fhir = new FhirClient(
            iss,
            http,
            new FhirClientSettings
            {
                PreferredFormat = ResourceFormat.Json,
                VerifyFhirVersion = true,
            }
        );

        try
        {
            var patient = await fhir.ReadAsync<Patient>($"Patient/{token.Patient}", ct: ct);

            return patient is null
                ? new CallbackOutcome.PatientNotFound(iss, token.Patient!)
                : new CallbackOutcome.Completed(
                    PatientSummary.From(patient),
                    patient.ToJson(pretty: true),
                    TokenFacts.From(token),
                    tokenJson,
                    patientUrl
                );
        }
        catch (FhirOperationException ex)
        {
            LogPatientReadFailed(ex, token.Patient);
            return new CallbackOutcome.PatientReadFailed(
                (int)ex.Status,
                Describe(ex.Outcome) ?? ex.Message
            );
        }
        catch (NotSupportedException ex)
        {
            // FhirClientSettings.VerifyFhirVersion throws when the server is not R4.
            return new CallbackOutcome.IncompatibleFhirVersion(iss, ex.Message);
        }
    }

    private static string? Describe(OperationOutcome? outcome) =>
        outcome?.Issue is { Count: > 0 } issues
            ? string.Join(
                "; ",
                issues.Select(i => i.Details?.Text ?? i.Diagnostics ?? i.Code.GetLiteral())
            )
            : null;
}
