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
    public PatientSummary Summary { get; private set; } = default!;

    public string RawJson { get; private set; } = "";

    /// <summary>Who started this launch, or why the app cannot say.</summary>
    public string WhoLaunchedIt { get; private set; } = "";

    /// <summary>The panel this page was asked for, or null when it was asked for none.</summary>
    public ChartOutcome? Panel { get; private set; }

    /// <summary>Where the panel links point. The launch is named the same way every time.</summary>
    public string Link(ChartPanel panel) =>
        $"/summary?id={Uri.EscapeDataString(LaunchId)}"
        + $"&patient={Uri.EscapeDataString(PatientId)}"
        + $"&show={panel.Slug}";

    private string LaunchId { get; set; } = "";

    private string PatientId { get; set; } = "";

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
        var resolution = cache.Resolve(BrowserSession.Current(HttpContext), id, patient, clock);

        switch (resolution)
        {
            case LaunchResolution.Resolved(var view):
                Summary = view.Rendered.Summary;
                RawJson = view.Rendered.RawJson;
                WhoLaunchedIt = LaunchMessages.WhoLaunchedIt(view.Rendered);
                (LaunchId, PatientId) = (view.Facts.LaunchId, view.Facts.PatientId);

                // By name, not by context: the credential stays below this page.
                if (ChartPanel.For(show) is { } panel)
                    Panel = await chart.ReadAsync(
                        BrowserSession.Current(HttpContext),
                        id,
                        patient,
                        panel,
                        ct
                    );

                return Page();

            case LaunchResolution.PatientMismatch(var facts, var claimed):
                // Unlike an expiry, this is worth knowing happened at all.
                await log.RecordAsync(Refused(facts, claimed), ct);
                return Relaunch(patient);

            case LaunchResolution.Unknown
            or LaunchResolution.Expired:
                return Relaunch(patient);

            default:
                throw new UnreachableException($"{resolution.GetType().Name} is not a resolution.");
        }
    }

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

    private RedirectToPageResult Relaunch(string? patient) =>
        RedirectToPage("/Error", new { message = LaunchMessages.Relaunch(patient) });

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
