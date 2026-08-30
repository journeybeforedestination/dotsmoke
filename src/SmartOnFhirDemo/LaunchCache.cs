using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo;

/// <summary>
/// The only two things that outlive a request: a launch in flight, and — for the narrated
/// launch alone — the finished transcript the reader is still walking through. Both are
/// keyed by the OAuth state, held in memory, and gone after five minutes.
///
/// A launch in flight holds the PKCE verifier, which is why it is claimed rather than read:
/// it is spent by the token exchange and must not survive it. A transcript holds no
/// credential at all — <see cref="SmartLaunch"/> removes the access token before returning
/// anything that can reach here — but it does hold patient data, which is why it expires.
/// </summary>
public static class LaunchCache
{
    /// <summary>Long enough to read a page, short enough that nothing lingers.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Only the state and PKCE verifier need to survive the trip through the EHR.</summary>
    public static void Remember(this IMemoryCache cache, LaunchOutcome.Prepared prepared) =>
        cache.Set(Smart.CacheKey(prepared.State), prepared.Session, Lifetime);

    /// <summary>Reads a launch in flight without spending it, for the page that pauses before the exchange.</summary>
    public static LaunchState? PeekLaunch(this IMemoryCache cache, string? state) =>
        string.IsNullOrEmpty(state) ? null
        : cache.TryGetValue(Smart.CacheKey(state), out LaunchState? launch) ? launch
        : null;

    /// <summary>Takes the launch this callback belongs to out of the cache. It is single use.</summary>
    public static LaunchState? ClaimLaunch(this IMemoryCache cache, string? state)
    {
        if (cache.PeekLaunch(state) is not { } launch)
            return null;

        cache.Remove(Smart.CacheKey(state!));
        return launch;
    }

    public static void RememberTranscript(
        this IMemoryCache cache,
        string state,
        CallbackOutcome.Completed completed
    ) => cache.Set(Smart.TranscriptKey(state), completed, Lifetime);

    /// <summary>The finished launch a walkthrough page is reading, or null once it has expired.</summary>
    public static CallbackOutcome.Completed? Transcript(this IMemoryCache cache, string? state) =>
        string.IsNullOrEmpty(state) ? null
        : cache.TryGetValue(Smart.TranscriptKey(state), out CallbackOutcome.Completed? completed)
            ? completed
        : null;
}
