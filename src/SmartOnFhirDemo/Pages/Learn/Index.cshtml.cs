using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// Step 1: the trust check, discovery and the authorization request, and then a stop to
/// explain them instead of the redirect a real app would make. The launch itself is real;
/// only the pause is new.
/// </summary>
public class IndexModel(
    SmartLaunch smart,
    IOptions<SmartOptions> options,
    IMemoryCache cache,
    AccessLog log,
    TimeProvider clock
) : LearnPage(cache, log, clock)
{
    public IReadOnlyList<LaunchStep> Steps { get; private set; } = [];

    /// <summary>Where the continue button goes: the EHR, where an app that did not pause would redirect.</summary>
    public string AuthorizeUrl { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string? iss, string? launch, CancellationToken ct)
    {
        var redirectUri = options.Value.Url("/learn/callback");

        var outcome = await smart.BeginAsync(iss, launch, redirectUri, ct);

        if (outcome is not LaunchOutcome.Prepared prepared)
            return Fail(LaunchMessages.For(outcome));

        Cache.Remember(prepared);
        Steps = LaunchTranscript.BeforeTheRedirect(prepared);
        AuthorizeUrl = prepared.AuthorizeUrl;
        return Page();
    }
}
