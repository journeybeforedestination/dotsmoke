using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>Who was driving the launch, read back from what the launch recorded.</summary>
public class UserModel(IMemoryCache cache, AccessLog log, TimeProvider clock)
    : LearnPage(cache, log, clock)
{
    public LaunchStep Step { get; private set; } = default!;

    public string LaunchId { get; private set; } = "";

    public string PatientId { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string? id, string? patient, CancellationToken ct)
    {
        if (await LaunchAsync(id, patient, ct) is not { } view)
            return Relaunch(patient);

        (LaunchId, PatientId) = (view.Facts.LaunchId, view.Facts.PatientId);
        Step = LaunchTranscript.WhoLaunchedThis(view.Rendered);
        return Page();
    }
}
