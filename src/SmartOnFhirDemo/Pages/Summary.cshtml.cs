using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages;

/// <summary>
/// Where a launch lands, and — unlike the callback it replaced — a page you can come back
/// to. It fetches nothing: the launch was established at the callback, and this resolves
/// it from the three values that name it.
/// </summary>
public class SummaryModel(IMemoryCache cache, Chart chart, AccessLog log, TimeProvider clock)
    : PageModel
{
    /// <summary>Who started this launch, or why the app cannot say.</summary>
    public string WhoLaunchedIt { get; private set; } = "";

    /// <summary>The patient, the panels this page offers, and the resource behind them.</summary>
    public AppView App { get; private set; } = default!;

    /// <param name="patient">
    /// The patient this page believes it is showing. Carried so the server can disagree:
    /// rendering one patient under another's banner is the failure the session design
    /// exists to prevent, and a page that never says what it thinks it is showing cannot
    /// be caught doing it.
    /// </param>
    /// <param name="show">Which panel of the chart to read, or none for the summary alone.</param>
    public async Task<IActionResult> OnGetAsync(
        string? id,
        string? patient,
        string? show,
        CancellationToken ct
    )
    {
        if (await ResolveAsync(id, patient, ct) is not { } view)
            return Relaunch(patient);

        WhoLaunchedIt = LaunchMessages.WhoLaunchedIt(view.Rendered);

        // By name, not by context: the credential stays below this page.
        App = new AppView(
            view.Rendered.Summary,
            await PanelsAsync(view, show, ct),
            view.Rendered.RawJson
        );

        return Page();
    }

    /// <summary>
    /// The pane alone, for a tab that swapped rather than navigated. It resolves the
    /// launch through the same guard the page does — a request that reached the chart
    /// without passing it would be a way to read a patient this launch does not name.
    ///
    /// A refusal answers with a status rather than a redirect: the script's fallback is
    /// to navigate for real, which lands on the page that explains what went wrong. Which
    /// refusal it was does not reach the browser, and does not need to — both mean this
    /// launch will not serve this pane.
    /// </summary>
    public async Task<IActionResult> OnGetPaneAsync(
        string? id,
        string? patient,
        string? show,
        CancellationToken ct
    ) =>
        await ResolveAsync(id, patient, ct) is { } view
            ? Partial("_Pane", await PanelsAsync(view, show, ct))
            : new StatusCodeResult(StatusCodes.Status409Conflict);

    /// <summary>
    /// The launch these three values name, or null once the refusal has been recorded.
    /// Both handlers come through here, because the page and its pane check the same
    /// thing and differ only in what they return when the check fails.
    /// </summary>
    private async Task<LaunchView?> ResolveAsync(string? id, string? patient, CancellationToken ct)
    {
        var resolution = cache.Resolve(BrowserSession.Current(HttpContext), id, patient, clock);

        switch (resolution)
        {
            case LaunchResolution.Resolved(var view):
                return view;

            case LaunchResolution.PatientMismatch(var facts, var claimed):
                // Unlike an expiry, this is worth knowing happened at all.
                await log.RecordAsync(Refused(facts, claimed), ct);
                return null;

            case LaunchResolution.Unknown
            or LaunchResolution.Expired:
                return null;

            default:
                throw new UnreachableException($"{resolution.GetType().Name} is not a resolution.");
        }
    }

    private Task<ChartView> PanelsAsync(LaunchView view, string? show, CancellationToken ct) =>
        chart.ViewAsync("/summary", BrowserSession.Current(HttpContext), view.Facts, show, ct);

    /// <summary>
    /// A read that was refused before anything was asked of the EHR. The patient recorded
    /// is the one the page claimed rather than the one the launch holds, because the
    /// question this row answers is whose chart someone was about to be shown.
    /// </summary>
    private AccessLogEntry Refused(LaunchFacts facts, string claimed) =>
        new(
            clock.GetUtcNow(),
            facts.IssuerOrigin,
            claimed,
            facts.FhirUser,
            "Patient",
            $"Patient/{claimed}",
            AccessOutcome.LaunchMismatch,
            // Nothing was sent, so there is no status to report.
            Status: null
        );

    /// <summary>
    /// The sentence names the patient, so it travels in TempData rather than in a URL that
    /// a browser would keep. See <see cref="ErrorModel"/>.
    /// </summary>
    private RedirectToPageResult Relaunch(string? patient)
    {
        TempData[ErrorModel.Key] = LaunchMessages.Relaunch(patient);
        return RedirectToPage("/Error");
    }

    /// <summary>
    /// This URL is stable and revisitable now, which the one-shot callback was not. That
    /// makes what a browser or a proxy keeps of it worth saying out loud: nothing.
    /// </summary>
    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store";
        base.OnPageHandlerExecuting(context);
    }
}
