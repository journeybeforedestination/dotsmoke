using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// The last stop, which stops narrating: a sentence saying the launch is over, and then
/// the app the handshake was for, rendered from the same view <c>/summary</c> renders.
/// That sharing is the point — the walkthrough claims to be narrating this app, and two
/// copies of the markup would be where the claim stopped being true.
/// </summary>
public class PatientModel(IMemoryCache cache, Chart chart, TimeProvider clock)
    : LearnPage(cache, clock)
{
    public LaunchStep Step { get; private set; } = default!;

    /// <summary>The app this walkthrough ends on, which is the app /summary is.</summary>
    public AppView App { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(
        string? id,
        string? patient,
        string? show,
        CancellationToken ct
    )
    {
        if (Launch(id, patient) is not { } view)
            return Relaunch(patient);

        Step = LaunchTranscript.TheApp();
        App = new AppView(
            view.Rendered.Summary,
            await chart.ViewAsync(
                "/learn/patient",
                BrowserSession.Current(HttpContext),
                view.Facts,
                show,
                ct
            ),
            view.Rendered.RawJson
        );

        return Page();
    }
}
