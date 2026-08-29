using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    /// <summary>Ready to hand the browser to the EHR. The session must outlive the redirect.</summary>
    public sealed record Prepared(string AuthorizeUrl, string State, LaunchState Session) : LaunchOutcome;

    public sealed record MissingParameters : LaunchOutcome;

    public sealed record DiscoveryFailed(string WellKnown, string Reason) : LaunchOutcome;
}

/// <summary>How the callback step ended.</summary>
public abstract record CallbackOutcome
{
    private CallbackOutcome() { }

    public sealed record Completed(PatientSummary Summary, string RawJson) : CallbackOutcome;

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
public sealed class SmartLaunch(
    IHttpClientFactory clients,
    IOptions<SmartOptions> options,
    ILogger<SmartLaunch> log)
{
    private SmartOptions Options => options.Value;

    /// <summary>Discover the EHR's OAuth endpoints and build the authorization request.</summary>
    public async Task<LaunchOutcome> BeginAsync(
        string? iss, string? launch, string redirectUri, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(launch))
            return new LaunchOutcome.MissingParameters();

        var wellKnown = $"{iss.TrimEnd('/')}/.well-known/smart-configuration";

        SmartConfiguration? config;
        try
        {
            config = await clients.CreateClient().GetFromJsonAsync<SmartConfiguration>(wellKnown, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            log.LogWarning(ex, "SMART discovery failed for {WellKnown}", wellKnown);
            return new LaunchOutcome.DiscoveryFailed(wellKnown, ex.Message);
        }

        if (config is null)
            return new LaunchOutcome.DiscoveryFailed(wellKnown, "the server returned an empty configuration");

        var (verifier, challenge) = Smart.NewPkce();
        var state = Smart.NewState();

        return new LaunchOutcome.Prepared(
            Smart.BuildAuthorizeUrl(config, Options, redirectUri, iss, launch, state, challenge),
            state,
            new LaunchState(iss, config.TokenEndpoint, verifier, redirectUri));
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
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
            return new CallbackOutcome.AuthorizationDenied(errorDescription ?? error);

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return new CallbackOutcome.MissingParameters();

        if (launch is null)
            return new CallbackOutcome.UnknownLaunch();

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = launch.RedirectUri,
            ["code_verifier"] = launch.CodeVerifier,
            ["client_id"] = Options.ClientId,
        });

        using var response = await clients.CreateClient().PostAsync(launch.TokenEndpoint, form, ct);
        if (!response.IsSuccessStatusCode)
            return new CallbackOutcome.TokenExchangeFailed(
                (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            return new CallbackOutcome.NoAccessToken();

        if (string.IsNullOrEmpty(token.Patient))
            return new CallbackOutcome.NoPatientContext();

        return await ReadPatientAsync(launch.Iss, token, ct);
    }

    private async Task<CallbackOutcome> ReadPatientAsync(
        string iss, TokenResponse token, CancellationToken ct)
    {
        var http = clients.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var fhir = new FhirClient(iss, http, new FhirClientSettings
        {
            PreferredFormat = ResourceFormat.Json,
            VerifyFhirVersion = true,
        });

        try
        {
            var patient = await fhir.ReadAsync<Patient>($"Patient/{token.Patient}", ct: ct);

            return patient is null
                ? new CallbackOutcome.PatientNotFound(iss, token.Patient!)
                : new CallbackOutcome.Completed(PatientSummary.From(patient), patient.ToJson(pretty: true));
        }
        catch (FhirOperationException ex)
        {
            log.LogWarning(ex, "Reading Patient/{PatientId} failed", token.Patient);
            return new CallbackOutcome.PatientReadFailed((int)ex.Status, Describe(ex.Outcome) ?? ex.Message);
        }
        catch (NotSupportedException ex)
        {
            // FhirClientSettings.VerifyFhirVersion throws when the server is not R4.
            return new CallbackOutcome.IncompatibleFhirVersion(iss, ex.Message);
        }
    }

    private static string? Describe(OperationOutcome? outcome) =>
        outcome?.Issue is { Count: > 0 } issues
            ? string.Join("; ", issues.Select(i => i.Details?.Text ?? i.Diagnostics ?? i.Code.GetLiteral()))
            : null;
}
