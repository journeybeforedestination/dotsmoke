using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>The last stop: the FHIR read the whole handshake existed to authorize.</summary>
public class PatientModel(IMemoryCache cache, TimeProvider clock) : LearnPage(cache, clock)
{
    public LaunchStep Step { get; private set; } = default!;

    public PatientSummary Summary { get; private set; } = default!;

    public IActionResult OnGet(string? id, string? patient)
    {
        if (Launch(id, patient) is not { } view)
            return Relaunch(patient);

        Step = LaunchTranscript.ThePatientRead(view.Rendered);
        Summary = view.Rendered.Summary;
        return Page();
    }
}
