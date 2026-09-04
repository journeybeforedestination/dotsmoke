using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// The last stop, which stops narrating: a sentence saying the launch is over, and then
/// the app the handshake was for. Everything above it explained a step; this renders the
/// thing the steps were for.
/// </summary>
public class PatientModel(IMemoryCache cache, Chart chart, AccessLog log, TimeProvider clock)
    : LearnPage(cache, log, clock)
{
    public LaunchStep Step { get; private set; } = default!;

    public AppView App { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(
        string? id,
        string? patient,
        string? show,
        CancellationToken ct
    )
    {
        if (await LaunchAsync(id, patient, ct) is not { } view)
            return Relaunch(patient);

        Step = LaunchTranscript.TheApp();
        App = new AppView(
            view.Rendered.Summary,
            await PanelsAsync(view, show, ct),
            view.Rendered.RawJson
        );

        return Page();
    }

    /// <summary>
    /// The pane alone, for a tab that swapped rather than navigated. It resolves the launch
    /// through the same guard the page does — a request that reached the chart without
    /// passing it would be a way to read a patient this launch does not name. A refusal
    /// answers with a status rather than a redirect: the script's fallback is to navigate for
    /// real, which lands on the page that explains what went wrong.
    /// </summary>
    public async Task<IActionResult> OnGetPaneAsync(
        string? id,
        string? patient,
        string? show,
        CancellationToken ct
    ) =>
        await LaunchAsync(id, patient, ct) is { } view
            ? Partial("_Pane", await PanelsAsync(view, show, ct))
            : new StatusCodeResult(StatusCodes.Status409Conflict);

    private Task<ChartView> PanelsAsync(LaunchView view, string? show, CancellationToken ct) =>
        chart.ViewAsync(BrowserSession.Current(HttpContext), view.Facts, show, ct);
}
