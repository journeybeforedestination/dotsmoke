using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// The pause the plain callback cannot take: the authorization code has arrived and has
/// not been spent. The GET explains what is about to happen; the POST is what actually
/// exchanges the code, uses the access token once, and keeps only the redacted account
/// of it for the two pages that follow.
/// </summary>
public class CallbackModel(SmartLaunch smart, IMemoryCache cache) : LearnPage(cache)
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
        var outcome = await smart.CompleteAsync(
            Code,
            State,
            error: null,
            errorDescription: null,
            Cache.ClaimLaunch(State),
            ct
        );

        if (outcome is not CallbackOutcome.Completed completed)
            return Fail(LaunchMessages.For(outcome));

        // The access token was used and discarded inside CompleteAsync. What is kept here
        // never held it.
        Cache.RememberTranscript(State!, completed);
        return RedirectToPage("/Learn/Token", new { state = State });
    }
}
