using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo;

/// <summary>
/// Everything that outlives a request: a launch in flight, the finished transcript the
/// narrated launch is still walking through, and — since the summary became a page you
/// can come back to — the established launches a browser is holding open.
///
/// A launch in flight holds the PKCE verifier, which is why it is claimed rather than read:
/// it is spent by the token exchange and must not survive it. A transcript holds no
/// credential at all — <see cref="SmartLaunch"/> removes the access token before returning
/// anything that can reach here — but it does hold patient data, which is why it expires.
/// An established launch holds both, and expires when the EHR said the token does.
///
/// This stays <see cref="IMemoryCache"/> rather than becoming ASP.NET's session: ISession
/// stores byte arrays, so a launch context would have to be serialised, and this is
/// already the right shape.
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

    // ---- Established launches ---------------------------------------------

    /// <summary>
    /// Files a finished launch against the browser that started it. The credential and the
    /// account of the launch are stored as one entry so they cannot outlive each other, and
    /// the entry expires when the EHR said the token stops working.
    /// </summary>
    public static void RememberLaunch(
        this IMemoryCache cache,
        string sid,
        LaunchContext context,
        CallbackOutcome.Completed rendered
    ) =>
        cache.Set(
            Smart.ContextKey(sid, context.LaunchId),
            new EstablishedLaunch(context, rendered),
            context.ExpiresAt
        );

    /// <summary>
    /// The live launch a request names, credential included — for the code that makes FHIR
    /// requests with it, and for nothing else. Null unless the cookie, the URL and the
    /// clock all agree.
    /// </summary>
    public static LaunchContext? Context(
        this IMemoryCache cache,
        string? sid,
        string? launchId,
        TimeProvider clock
    ) => cache.Established(sid, launchId, clock)?.Context;

    /// <summary>
    /// The same launch as a page may know it. The expiry is checked here against the
    /// injected clock rather than left to the cache's own housekeeping, so a launch is
    /// gone the moment its token is rather than whenever the entry is next swept.
    /// </summary>
    public static LaunchView? View(
        this IMemoryCache cache,
        string? sid,
        string? launchId,
        TimeProvider clock
    ) =>
        cache.Established(sid, launchId, clock) is { } launch
            ? new LaunchView(launch.Context.Facts, launch.Rendered)
            : null;

    private static EstablishedLaunch? Established(
        this IMemoryCache cache,
        string? sid,
        string? launchId,
        TimeProvider clock
    )
    {
        // Both, or nothing. The cookie says which browser, the launch id says which of
        // that browser's launches, and neither answers on its own.
        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(launchId))
            return null;

        return
            cache.TryGetValue(Smart.ContextKey(sid, launchId), out EstablishedLaunch? launch)
            && launch?.Context.ExpiresAt > clock.GetUtcNow()
            ? launch
            : null;
    }
}

/// <summary>
/// An established launch as the cache holds it. Not public, because the halves are for
/// different callers: <see cref="LaunchCache.Context"/> hands the credential to what makes
/// requests with it, and <see cref="LaunchCache.View"/> hands everything else a projection
/// with no credential on it.
/// </summary>
internal sealed record EstablishedLaunch(LaunchContext Context, CallbackOutcome.Completed Rendered);
