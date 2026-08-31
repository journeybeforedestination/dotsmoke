using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// The last stop: the FHIR read the whole handshake existed to authorize, and then the
/// reads that only became possible because the launch is still open. It ends where
/// <c>/summary</c> ends, on purpose — the narration is of the same app.
/// </summary>
public class PatientModel(IMemoryCache cache, Chart chart, TimeProvider clock)
    : LearnPage(cache, clock)
{
    public LaunchStep Step { get; private set; } = default!;

    public PatientSummary Summary { get; private set; } = default!;

    /// <summary>The panels this page offers, and whichever one it was asked to show.</summary>
    public ChartView Chart { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(
        string? id,
        string? patient,
        string? show,
        CancellationToken ct
    )
    {
        if (Launch(id, patient) is not { } view)
            return Relaunch(patient);

        Step = LaunchTranscript.ThePatientRead(view.Rendered);
        Summary = view.Rendered.Summary;
        Chart = await chart.ViewAsync(
            "/learn/patient",
            BrowserSession.Current(HttpContext),
            view.Facts,
            show,
            ct
        );

        return Page();
    }
}
