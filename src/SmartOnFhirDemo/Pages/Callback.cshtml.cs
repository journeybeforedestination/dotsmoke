using System.Diagnostics;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages;

/// <summary>
/// Steps 2 and 3 of the SMART EHR launch. SmartLaunch trades the authorization code
/// for an access token and reads the patient; this page finds the launch the EHR is
/// returning from, and turns the outcome into a page.
/// </summary>
public class CallbackModel(SmartLaunch smart, IMemoryCache cache) : PageModel
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
        return await smart.CompleteAsync(code, state, error, errorDescription, Claim(state), ct) switch
        {
            CallbackOutcome.Completed(var summary, var rawJson) => Render(summary, rawJson),

            CallbackOutcome.AuthorizationDenied(var reason) =>
                Fail($"The EHR refused the authorization request: {reason}"),

            CallbackOutcome.MissingParameters =>
                Fail("Missing 'code' or 'state'. Start the launch from the EHR rather than opening this URL directly."),

            CallbackOutcome.UnknownLaunch =>
                Fail("This launch has expired or was already completed. Start a new launch from the EHR."),

            CallbackOutcome.TokenExchangeFailed(var status, var reason) =>
                Fail($"Token exchange failed ({status}): {reason}"),

            CallbackOutcome.NoAccessToken =>
                Fail("The token endpoint returned no access token."),

            CallbackOutcome.NoPatientContext =>
                Fail("The token response carried no patient context. Use a Provider EHR Launch with a patient selected."),

            CallbackOutcome.PatientNotFound(var iss, var patientId) =>
                Fail($"Patient/{patientId} was not found on {iss}."),

            CallbackOutcome.PatientReadFailed(var status, var reason) =>
                Fail($"The FHIR server returned {status}: {reason}"),

            CallbackOutcome.IncompatibleFhirVersion(var iss, var reason) =>
                Fail($"The FHIR server at {iss} is not compatible with FHIR {ModelInfo.Version}: {reason}"),

            var outcome => throw new UnreachableException($"Unhandled callback outcome: {outcome.GetType().Name}."),
        };
    }

    /// <summary>Takes the launch this callback belongs to out of the cache. It is single use.</summary>
    private LaunchState? Claim(string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;

        var key = Smart.CacheKey(state);
        if (!cache.TryGetValue(key, out LaunchState? launch)) return null;

        cache.Remove(key);
        return launch;
    }

    private IActionResult Render(PatientSummary summary, string rawJson)
    {
        Summary = summary;
        RawJson = rawJson;
        return Page();
    }

    private IActionResult Fail(string message) => RedirectToPage("/Error", new { message });
}
