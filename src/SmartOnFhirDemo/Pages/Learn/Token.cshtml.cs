using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// What the token endpoint said, and what the app did with it. Two steps on one page
/// because they are one moment: the credential arriving is what forces the question of
/// where it may live.
/// </summary>
public class TokenModel(IMemoryCache cache, AccessLog log, TimeProvider clock)
    : LearnPage(cache, log, clock)
{
    public IReadOnlyList<LaunchStep> Steps { get; private set; } = [];

    public string LaunchId { get; private set; } = "";

    public string PatientId { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string? id, string? patient, CancellationToken ct)
    {
        if (await LaunchAsync(id, patient, ct) is not { } view)
            return Relaunch(patient);

        (LaunchId, PatientId) = (view.Facts.LaunchId, view.Facts.PatientId);
        Steps =
        [
            LaunchTranscript.TheTokenResponse(view.Rendered),
            LaunchTranscript.TheSessionItStarts(view.Facts),
        ];
        return Page();
    }
}
