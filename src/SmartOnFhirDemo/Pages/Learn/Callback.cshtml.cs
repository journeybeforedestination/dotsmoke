using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// The pause the plain callback cannot take: the authorization code has arrived and has
/// not been spent. The GET explains what is about to happen; the POST is what actually
/// exchanges the code, uses the access token once, and keeps only the redacted account
/// of it for the two pages that follow.
/// </summary>
public class CallbackModel(SmartLaunch smart, IMemoryCache cache, TimeProvider clock)
    : LearnPage(cache, clock)
{
    public LaunchStep Step { get; private set; } = default!;

    [BindProperty]
    public string? Code { get; set; }

    [BindProperty]
    public string? State { get; set; }

    public IActionResult OnGet(
        string? code,
        string? state,
        string? error,
        [FromQuery(Name = "error_description")] string? errorDescription
    )
    {
        if (!string.IsNullOrEmpty(error))
            return Fail(LaunchMessages.AuthorizationDenied(errorDescription ?? error));

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Fail(LaunchMessages.MissingCallbackParameters);

        // Peeked, not claimed: the exchange has not happened yet, and the launch has to
        // still be here when the reader presses continue.
        if (Cache.PeekLaunch(state) is not { } launch)
            return Fail(LaunchMessages.UnknownLaunch);

        (Code, State) = (code, state);
        Step = LaunchTranscript.TheCodeCameBack(code, state, launch);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var (outcome, context) = await smart.CompleteAsync(
            Code,
            State,
            error: null,
            errorDescription: null,
            Cache.ClaimLaunch(State),
            ct
        );

        if (outcome is not CallbackOutcome.Completed completed || context is null)
            return Fail(LaunchMessages.For(outcome));

        // The narrated launch establishes a session exactly as the plain one does, because
        // it is narrating the same app. Step 6 is where the reader is told so — and the
        // steps after it read a live launch rather than a keepsake of one.
        Cache.RememberLaunch(BrowserSession.Establish(HttpContext), context, completed, Clock);

        return RedirectToPage(
            "/Learn/Token",
            new { id = context.LaunchId, patient = context.PatientId }
        );
    }
}
