using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace SmartOnFhirDemo.Pages;

/// <summary>
/// Steps 2 and 3 of the SMART EHR launch: trade the authorization code for an access
/// token, read the patient it points at, and render. The token is never stored.
/// </summary>
public class CallbackModel(
    IHttpClientFactory clients,
    IMemoryCache cache,
    IOptions<SmartOptions> options,
    ILogger<CallbackModel> log) : PageModel
{
    public PatientSummary Summary { get; private set; } = default!;
    public string RawJson { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(
        string? code,
        string? state,
        string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
            return Fail($"The EHR refused the authorization request: {errorDescription ?? error}");

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Fail("Missing 'code' or 'state'. Start the launch from the EHR rather than opening this URL directly.");

        if (!cache.TryGetValue(Smart.CacheKey(state), out LaunchState? launch) || launch is null)
            return Fail("This launch has expired or was already completed. Start a new launch from the EHR.");

        cache.Remove(Smart.CacheKey(state));

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = launch.RedirectUri,
            ["code_verifier"] = launch.CodeVerifier,
            ["client_id"] = options.Value.ClientId,
        });

        using var response = await clients.CreateClient().PostAsync(launch.TokenEndpoint, form, ct);
        if (!response.IsSuccessStatusCode)
            return Fail($"Token exchange failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            return Fail("The token endpoint returned no access token.");

        if (string.IsNullOrEmpty(token.Patient))
            return Fail("The token response carried no patient context. Use a Provider EHR Launch with a patient selected.");

        var http = clients.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var fhir = new FhirClient(launch.Iss, http, new FhirClientSettings
        {
            PreferredFormat = ResourceFormat.Json,
            VerifyFhirVersion = true,
        });

        try
        {
            var patient = await fhir.ReadAsync<Patient>($"Patient/{token.Patient}", ct: ct);
            if (patient is null)
                return Fail($"Patient/{token.Patient} was not found on {launch.Iss}.");

            Summary = PatientSummary.From(patient);
            RawJson = patient.ToJson(pretty: true);
            return Page();
        }
        catch (FhirOperationException ex)
        {
            log.LogWarning(ex, "Reading Patient/{PatientId} failed", token.Patient);
            return Fail($"The FHIR server returned {(int)ex.Status}: {Describe(ex.Outcome) ?? ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            // FhirClientSettings.VerifyFhirVersion throws when the server is not R4.
            return Fail($"The FHIR server at {launch.Iss} is not compatible with FHIR {ModelInfo.Version}: {ex.Message}");
        }
    }

    private static string? Describe(OperationOutcome? outcome) =>
        outcome?.Issue is { Count: > 0 } issues
            ? string.Join("; ", issues.Select(i => i.Details?.Text ?? i.Diagnostics ?? i.Code.GetLiteral()))
            : null;

    private IActionResult Fail(string message) => RedirectToPage("/Error", new { message });
}
