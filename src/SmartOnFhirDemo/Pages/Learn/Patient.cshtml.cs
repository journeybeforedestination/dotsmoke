using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// The last stop, which stops narrating: a sentence saying the launch is over, and then
/// the app the handshake was for. Everything above it explained a step; this renders the
/// thing the steps were for.
/// </summary>
public class PatientModel(
    IMemoryCache cache,
    Chart chart,
    AccessLog log,
    AccessLogReader access,
    TimeProvider clock
) : LearnPage(cache, log, clock)
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
            // After the panels, not before: reading one is itself a logged request, and a
            // section built first would be missing the read the page it is on just made.
            await AccessAsync(view, ct),
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

    /// <summary>
    /// The access section alone, for the script to refresh after a tab swapped the pane. Same
    /// guard, same refusal: this is a launch's own trail, and no launch may be handed
    /// another's — which is exactly what the launch-scoped query below rests on.
    /// </summary>
    public async Task<IActionResult> OnGetAccessAsync(
        string? id,
        string? patient,
        CancellationToken ct
    ) =>
        await LaunchAsync(id, patient, ct) is { } view
            ? Partial("_Access", await AccessAsync(view, ct))
            : new StatusCodeResult(StatusCodes.Status409Conflict);

    /// <summary>One row past what the section shows, so a list that was cut can say so.</summary>
    private async Task<AccessView> AccessAsync(LaunchView view, CancellationToken ct) =>
        AccessView.Of(
            view.Facts,
            await access.ForLaunchAsync(view.Facts.LaunchId, AccessView.Rows + 1, ct)
        );

    private Task<ChartView> PanelsAsync(LaunchView view, string? show, CancellationToken ct) =>
        chart.ViewAsync(BrowserSession.Current(HttpContext), view.Facts, show, ct);
}
