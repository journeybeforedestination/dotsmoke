using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>Who was driving the launch, read back from what the launch recorded.</summary>
public class UserModel(IMemoryCache cache, TimeProvider clock) : LearnPage(cache, clock)
{
    public LaunchStep Step { get; private set; } = default!;

    public string LaunchId { get; private set; } = "";

    public string PatientId { get; private set; } = "";

    public IActionResult OnGet(string? id, string? patient)
    {
        if (Launch(id, patient) is not { } view)
            return Relaunch(patient);

        (LaunchId, PatientId) = (view.Facts.LaunchId, view.Facts.PatientId);
        Step = LaunchTranscript.WhoLaunchedThis(view.Rendered);
        return Page();
    }
}
