using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.Pages.Learn;

/// <summary>
/// The narrated counterpart to <c>/launch</c>. It runs the identical first step — the
/// same trust check, the same discovery, the same authorization request — and then stops
/// to explain it instead of redirecting. The launch itself is real; only the pause is new.
/// </summary>
public class IndexModel(SmartLaunch smart, IMemoryCache cache) : LearnPage(cache)
{
    public IReadOnlyList<LaunchStep> Steps { get; private set; } = [];

    /// <summary>Where the continue button goes: the EHR, exactly as the plain launch would redirect.</summary>
    public string AuthorizeUrl { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string? iss, string? launch, CancellationToken ct)
    {
        // A separate redirect URI from the plain launch, so the EHR brings the browser
        // back to the narrated callback and the two flows never cross.
        var redirectUri = $"{Request.Scheme}://{Request.Host}/learn/callback";

        var outcome = await smart.BeginAsync(iss, launch, redirectUri, ct);

        if (outcome is not LaunchOutcome.Prepared prepared)
            return Fail(LaunchMessages.For(outcome));

        Cache.Remember(prepared);
        Steps = LaunchTranscript.BeforeTheRedirect(prepared);
        AuthorizeUrl = prepared.AuthorizeUrl;
        return Page();
    }
}
