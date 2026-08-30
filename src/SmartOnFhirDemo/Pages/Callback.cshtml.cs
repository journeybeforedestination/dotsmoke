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
        CancellationToken ct
    )
    {
        var outcome = await smart.CompleteAsync(
            code,
            state,
            error,
            errorDescription,
            cache.ClaimLaunch(state),
            ct
        );

        return outcome is CallbackOutcome.Completed completed
            ? Render(completed)
            : Fail(LaunchMessages.For(outcome));
    }

    private IActionResult Render(CallbackOutcome.Completed completed)
    {
        Summary = completed.Summary;
        RawJson = completed.RawJson;
        return Page();
    }

    private IActionResult Fail(string message) => RedirectToPage("/Error", new { message });
}
