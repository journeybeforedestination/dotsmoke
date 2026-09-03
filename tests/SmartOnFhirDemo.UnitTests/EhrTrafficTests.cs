using System.Net;
using System.Threading.RateLimiting;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The bound this app puts on its own traffic to an EHR. What matters is that a call it
/// will not make fails as a sentence rather than hanging or escaping as an unhandled
/// exception — there are two ways the limiter declines, and both have to end the same way.
/// </summary>
public class EhrTrafficTests
{
    /// <summary>
    /// Short enough that a test asserting the wait expires does not spend the real five
    /// seconds, which is why the handler takes the wait rather than reading the constant.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task A_call_under_the_limit_passes_through_untouched()
    {
        using var limiter = EhrTraffic.Limiter();
        using var client = Client(limiter);

        using var response = await client.GetAsync(
            "https://ehr.example/fhir/Patient/1",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_call_that_waits_too_long_for_a_slot_is_refused_rather_than_held()
    {
        using var limiter = OnePermit(QueueLimit: int.MaxValue);

        // A call that holds the one permit until this test lets it go is what the second
        // one waits behind.
        var occupied = new TaskCompletionSource();
        using var blocking = Client(limiter, occupied.Task);
        using var client = Client(limiter);

        var held = Send(blocking, "1");

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => Send(client, "2"));

        Assert.Contains("no slot came free", refused.Message);

        await ReleaseAsync(occupied, held);
    }

    [Fact]
    public async Task A_full_queue_is_refused_the_same_way_as_a_wait_that_expired()
    {
        // AcquireAsync does not throw when the queue is full — it hands back a lease that
        // was not acquired. A handler that caught only the cancellation would let this one
        // escape as an unhandled exception on a URL a stranger can open.
        using var limiter = OnePermit(QueueLimit: 0);

        var occupied = new TaskCompletionSource();
        using var blocking = Client(limiter, occupied.Task);
        using var client = Client(limiter);

        var held = Send(blocking, "1");

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => Send(client, "2"));

        Assert.Contains("in flight at once", refused.Message);

        await ReleaseAsync(occupied, held);
    }

    [Fact]
    public async Task A_reader_who_leaves_is_not_reported_as_an_ehr_that_could_not_be_reached()
    {
        // The reader going away and the wait expiring cancel the same linked token. Only
        // the second is this app's news to report; the first belongs to whoever left.
        using var limiter = OnePermit(QueueLimit: int.MaxValue);

        var occupied = new TaskCompletionSource();
        using var blocking = Client(limiter, occupied.Task);
        using var client = Client(limiter);
        using var gone = new CancellationTokenSource();

        var held = Send(blocking, "1");
        var waiting = client.GetAsync("https://ehr.example/fhir/Patient/2", gone.Token);

        await gone.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        await ReleaseAsync(occupied, held);
    }

    private static ConcurrencyLimiter OnePermit(int QueueLimit) =>
        new(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = QueueLimit });

    private static Task<HttpResponseMessage> Send(HttpClient client, string patient) =>
        client.GetAsync(
            $"https://ehr.example/fhir/Patient/{patient}",
            TestContext.Current.CancellationToken
        );

    /// <summary>
    /// Lets the permit-holding call finish and waits for it, so a blocked send does not
    /// outlive the test that started it.
    /// </summary>
    private static async Task ReleaseAsync(
        TaskCompletionSource occupied,
        Task<HttpResponseMessage> held
    )
    {
        occupied.SetResult();
        (await held).Dispose();
    }

    /// <param name="block">
    /// What the inner handler waits on before answering, so a test can hold a permit for
    /// as long as it needs one held.
    /// </param>
    private static HttpClient Client(ConcurrencyLimiter limiter, Task? block = null) =>
        new(new EhrTrafficHandler(limiter, Wait) { InnerHandler = new StubHandler(block) });

    private sealed class StubHandler(Task? block) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            if (block is not null)
                await block;

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
