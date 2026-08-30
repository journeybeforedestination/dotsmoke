using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>The last stop: the FHIR read the whole handshake existed to authorize.</summary>
public class PatientModel(IMemoryCache cache) : LearnPage(cache)
{
    public LaunchStep Step { get; private set; } = default!;

    public PatientSummary Summary { get; private set; } = default!;

    public IActionResult OnGet(string? state)
    {
        if (Transcript(state) is not { } completed)
            return Fail(LaunchMessages.ExpiredWalkthrough);

        Step = LaunchTranscript.ThePatientRead(completed);
        Summary = completed.Summary;
        return Page();
    }
}
