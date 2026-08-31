using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo;

/// <summary>
/// The two things that outlive a request: a launch in flight, and a launch that completed.
///
/// A launch in flight holds the PKCE verifier, which is why it is claimed rather than read:
/// it is spent by the token exchange and must not survive it. An established launch holds
/// the access token and the account of the launch that the pages render — both halves of
/// what the plain summary and the narrated walkthrough each need — and expires when the
/// EHR said the token does.
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

    // ---- Established launches ---------------------------------------------

    /// <summary>
    /// Files a finished launch against the browser that started it. The credential and the
    /// account of the launch are stored as one entry so they cannot outlive each other, and
    /// the entry expires when the EHR said the token stops working.
    ///
    /// As a lifetime rather than a moment, because the cache sweeps on the system clock
    /// while everything else here reads the injected one. They are the same clock in a
    /// deployment and different clocks under a test, and a duration means the same to both.
    /// </summary>
    public static void RememberLaunch(
        this IMemoryCache cache,
        string sid,
        LaunchContext context,
        CallbackOutcome.Completed rendered,
        TimeProvider clock
    )
    {
        // An EHR that hands back an already-expired token gets no launch rather than one
        // that cannot be resolved. The reader sees the same prompt either way.
        if (context.ExpiresAt - clock.GetUtcNow() is not { Ticks: > 0 } lifetime)
            return;

        cache.Set(
            Smart.ContextKey(sid, context.LaunchId),
            new EstablishedLaunch(context, rendered),
            lifetime
        );
    }

    /// <summary>
    /// The launch a page is asking for, or the reason there is not one. Three values have
    /// to agree: the cookie says which browser, the id says which of that browser's
    /// launches, and the patient id says what the page believes it is showing. The last is
    /// a parameter rather than a check a caller remembers to make, because forgetting it
    /// is the whole failure mode.
    /// </summary>
    public static LaunchResolution Resolve(
        this IMemoryCache cache,
        string? sid,
        string? launchId,
        string? patientId,
        TimeProvider clock
    )
    {
        var (launch, refused) = cache.Look(sid, launchId, patientId, clock);

        return refused
            ?? new LaunchResolution.Resolved(
                new LaunchView(launch!.Context.Facts, launch.Rendered)
            );
    }

    /// <summary>
    /// The same launch with its credential, for the code that makes requests with it. Kept
    /// apart from <see cref="Resolve"/> so that the method the pages call cannot return a
    /// token: what they get has no property to read one off.
    /// </summary>
    internal static LaunchContext? Credential(
        this IMemoryCache cache,
        string? sid,
        string? launchId,
        string? patientId,
        TimeProvider clock
    ) => cache.Look(sid, launchId, patientId, clock).Launch?.Context;

    private static (EstablishedLaunch? Launch, LaunchResolution? Refused) Look(
        this IMemoryCache cache,
        string? sid,
        string? launchId,
        string? patientId,
        TimeProvider clock
    )
    {
        if (
            string.IsNullOrEmpty(sid)
            || string.IsNullOrEmpty(launchId)
            || string.IsNullOrEmpty(patientId)
        )
            return (null, new LaunchResolution.Unknown());

        if (
            !cache.TryGetValue(Smart.ContextKey(sid, launchId), out EstablishedLaunch? launch)
            || launch is null
        )
            return (null, new LaunchResolution.Unknown());

        // Against the injected clock rather than left to the cache's own housekeeping, so a
        // launch is gone the moment its token is rather than whenever the entry is swept.
        // Once it has been swept this is unreachable and an expired launch reads as an
        // unknown one, which is honest: by then the app has nothing left to tell them apart.
        if (launch.Context.ExpiresAt <= clock.GetUtcNow())
            return (null, new LaunchResolution.Expired());

        if (!string.Equals(launch.Context.PatientId, patientId, StringComparison.Ordinal))
            return (null, new LaunchResolution.PatientMismatch(launch.Context.Facts, patientId));

        return (launch, null);
    }
}

/// <summary>
/// An established launch as the cache holds it. Not public, because the two halves are for
/// different callers: the credential is for the code that makes requests with it, and
/// <see cref="LaunchCache.Resolve"/> hands everything else a projection without one.
/// </summary>
internal sealed record EstablishedLaunch(LaunchContext Context, CallbackOutcome.Completed Rendered);
