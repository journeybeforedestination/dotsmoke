using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>Who was driving the launch, read back from the transcript.</summary>
public class UserModel(IMemoryCache cache) : LearnPage(cache)
{
    public LaunchStep Step { get; private set; } = default!;

    public string State { get; private set; } = "";

    public IActionResult OnGet(string? state)
    {
        if (Transcript(state) is not { } completed)
            return Fail(LaunchMessages.ExpiredWalkthrough);

        State = state!;
        Step = LaunchTranscript.WhoLaunchedThis(completed);
        return Page();
    }
}
