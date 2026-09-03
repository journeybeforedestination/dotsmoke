using System.Threading.RateLimiting;

namespace SmartOnFhirDemo;

/// <summary>
/// How much traffic this app will point at an EHR at once. The goal is narrow: this app
/// should never be the reason a public sandbox has a bad day. Constants rather than
/// configuration, following <see cref="LaunchCache.Entries"/> — a bound a reader can find
/// and believe beats one an operator can tune.
/// </summary>
public static class EhrTraffic
{
    /// <summary>
    /// How many requests to an EHR may be in flight at once, across the whole process. A
    /// launch makes its four calls in sequence, so this is roughly "how many readers at
    /// once".
    /// </summary>
    public const int InFlight = 4;

    /// <summary>
    /// How long a call will wait for a slot before giving up. Well inside the clients'
    /// 30-second timeout (Program.cs), so a wait that expires is never mistaken for the EHR
    /// itself timing out.
    /// </summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The reason a held-back call gives. It reaches a reader inside the app's existing
    /// "could not be reached" sentences: a new outcome across three hierarchies costs more
    /// than the wording does.
    /// </summary>
    public static string HeldBack =>
        $"this app keeps at most {InFlight} requests to an EHR in flight at once, and no "
        + $"slot came free within {MaxWait.TotalSeconds:0} seconds";

    /// <summary>
    /// The one limiter, which is why this hands back an instance rather than being a
    /// property: it must be registered as a singleton, and a second one would be a cap of
    /// <see cref="InFlight"/> per copy, which is no cap at all. See CLAUDE.md.
    /// </summary>
    public static ConcurrencyLimiter Limiter() =>
        new(
            new ConcurrencyLimiterOptions
            {
                PermitLimit = InFlight,

                // Effectively unbounded, because the wait is what refuses rather than the
                // depth: a caller queued behind a slow EHR gives up after MaxWait either
                // way, and a queue length is a third number a reader would have to hold.
                QueueLimit = int.MaxValue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }
        );
}

/// <summary>
/// Holds every outbound call to an EHR to <see cref="EhrTraffic.InFlight"/> at a time,
/// and refuses one that waits longer than <see cref="EhrTraffic.MaxWait"/> for its turn.
///
/// Concurrency rather than a rate, because it self-regulates: if the EHR slows down,
/// in-flight calls pile up and this app backs off. A rate cap keeps sending at the same rate
/// into a server that has started timing out.
///
/// <b>The limiter is a constructor argument, and that is load-bearing.</b> A handler that
/// built its own would get one per pooled handler chain, so the cap would be per chain and
/// the app would quietly send as much as it liked, failing nothing. The mirror of
/// <see cref="AccessLogHandler"/>'s rule: the launch must not be shared, the limiter must be.
///
/// The lease releases when the response headers arrive, not when the body is read —
/// <c>HttpClient</c> buffers the body above the handler chain.
/// </summary>
/// <param name="maxWait">
/// Taken rather than read from <see cref="EhrTraffic.MaxWait"/> so a test can assert the
/// wait expires without spending the real one.
/// </param>
public sealed class EhrTrafficHandler(ConcurrencyLimiter limiter, TimeSpan maxWait)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(maxWait);

        RateLimitLease lease;
        try
        {
            lease = await limiter.AcquireAsync(1, wait.Token);
        }
        // Only the wait expiring is something to report. If the caller's own token is
        // cancelled the reader has left, and that exception belongs to them.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(EhrTraffic.HeldBack);
        }

        // The other way AcquireAsync declines: a full queue hands back an unacquired lease
        // rather than throwing. Both paths have to end as an HttpRequestException, or one
        // escapes as an unhandled exception on a URL a stranger can open.
        using (lease)
        {
            if (!lease.IsAcquired)
                throw new HttpRequestException(EhrTraffic.HeldBack);

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
