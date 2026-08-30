using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>What the token endpoint said, read back from the transcript.</summary>
public class TokenModel(IMemoryCache cache) : LearnPage(cache)
{
    public LaunchStep Step { get; private set; } = default!;

    public string State { get; private set; } = "";

    public IActionResult OnGet(string? state)
    {
        if (Transcript(state) is not { } completed)
            return Fail(LaunchMessages.ExpiredWalkthrough);

        State = state!;
        Step = LaunchTranscript.TheTokenResponse(completed);
        return Page();
    }
}
