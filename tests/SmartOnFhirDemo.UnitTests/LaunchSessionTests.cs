using Microsoft.Extensions.Caching.Memory;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// What keeps two open patients apart. The end-to-end proof — two real launches down one
/// cookie jar — is an integration test and only runs where a launcher does; these are the
/// ones standing guard on every pull request, so they are the ones to read first.
/// </summary>
public class LaunchSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeProvider Clock = new FixedClock(Now);

    private const string Browser = "one-browser";

    private static LaunchContext Context(string patientId, TimeSpan? lifetime = null) =>
        new(
            $"launch-for-{patientId}",
            "https://ehr.example/r4/fhir",
            "https://ehr.example:443",
            patientId,
            "Practitioner/prac-1",
            Now + (lifetime ?? TimeSpan.FromHours(1)),
            "the-access-token"
        );

    private static CallbackOutcome.Completed Rendered(string patientId) =>
        new(
            new PatientSummary($"Patient {patientId}", null, null, null, null, null, null),
            "{}",
            new TokenFacts("Bearer", 3600, null, patientId, null, null, null),
            "{}",
            $"https://ehr.example/r4/fhir/Patient/{patientId}"
        );

    private static MemoryCache With(params LaunchContext[] contexts)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());

        foreach (var context in contexts)
            cache.RememberLaunch(Browser, context, Rendered(context.PatientId), Clock);

        return cache;
    }

    // ---- Two open patients ------------------------------------------------

    [Fact]
    public void Two_launches_in_one_browser_do_not_overwrite_each_other()
    {
        // The failure this exists to prevent: one cookie, two tabs, and the second
        // patient replacing the first while the first tab is still on screen.
        var (first, second) = (Context("pat-123"), Context("pat-456"));
        var cache = With(first, second);

        Assert.Equal(
            "pat-123",
            Resolved(cache.Resolve(Browser, first.LaunchId, "pat-123", Clock)).Facts.PatientId
        );
        Assert.Equal(
            "pat-456",
            Resolved(cache.Resolve(Browser, second.LaunchId, "pat-456", Clock)).Facts.PatientId
        );
    }

    [Fact]
    public void A_launch_belonging_to_another_browser_does_not_resolve()
    {
        var context = Context("pat-123");
        var cache = With(context);

        // The URL selects; it does not authenticate. Without the cookie it names nothing.
        Assert.IsType<LaunchResolution.Unknown>(
            cache.Resolve("another-browser", context.LaunchId, "pat-123", Clock)
        );
    }

    [Theory]
    [InlineData(null, "launch-for-pat-123", "pat-123")]
    [InlineData(Browser, null, "pat-123")]
    [InlineData(Browser, "launch-for-pat-123", null)]
    [InlineData(Browser, "never-issued", "pat-123")]
    public void A_launch_is_named_by_three_values_and_no_fewer(
        string? sid,
        string? launchId,
        string? patientId
    )
    {
        var cache = With(Context("pat-123"));

        Assert.IsType<LaunchResolution.Unknown>(cache.Resolve(sid, launchId, patientId, Clock));
    }

    // ---- The page and the launch disagreeing ------------------------------

    [Fact]
    public void A_page_showing_one_patient_cannot_resolve_another_patients_launch()
    {
        var context = Context("pat-456");
        var cache = With(context);

        var mismatch = Assert.IsType<LaunchResolution.PatientMismatch>(
            cache.Resolve(Browser, context.LaunchId, "pat-123", Clock)
        );

        // What the page claimed, which is whose chart someone was about to be shown.
        Assert.Equal("pat-123", mismatch.Claimed);
        Assert.Equal("pat-456", mismatch.Facts.PatientId);
    }

    [Fact]
    public void A_mismatch_is_told_apart_from_an_expiry_even_though_the_page_is_the_same()
    {
        var context = Context("pat-123", lifetime: TimeSpan.FromMinutes(5));
        var cache = With(context);

        var later = new FixedClock(Now + TimeSpan.FromMinutes(10));

        Assert.IsType<LaunchResolution.Expired>(
            cache.Resolve(Browser, context.LaunchId, "pat-123", later)
        );

        // Both reach the reader as the same prompt, naming the patient the page had been
        // showing so it is clear which launch to start again.
        Assert.Contains("pat-123", LaunchMessages.Relaunch("pat-123"));
    }

    [Fact]
    public void A_launch_outlives_neither_its_token_nor_a_second_of_it()
    {
        var context = Context("pat-123", lifetime: TimeSpan.FromMinutes(5));
        var cache = With(context);

        Assert.IsType<LaunchResolution.Resolved>(
            cache.Resolve(
                Browser,
                context.LaunchId,
                "pat-123",
                new FixedClock(context.ExpiresAt - TimeSpan.FromSeconds(1))
            )
        );

        Assert.IsType<LaunchResolution.Expired>(
            cache.Resolve(Browser, context.LaunchId, "pat-123", new FixedClock(context.ExpiresAt))
        );
    }

    // ---- What a resolved launch is allowed to carry -----------------------

    [Fact]
    public void A_resolved_launch_carries_the_facts_and_not_the_credential()
    {
        var context = Context("pat-123");
        var cache = With(context);

        var view = Resolved(cache.Resolve(Browser, context.LaunchId, "pat-123", Clock));

        Assert.Equal(context.LaunchId, view.Facts.LaunchId);
        Assert.Equal("https://ehr.example:443", view.Facts.IssuerOrigin);
        Assert.Equal("Practitioner/prac-1", view.Facts.FhirUser);

        // There is no property to read the token off, which is the guarantee: a page
        // cannot leak what it was never handed.
        Assert.DoesNotContain("the-access-token", view.Facts.ToString(), StringComparison.Ordinal);
    }

    private static LaunchView Resolved(LaunchResolution resolution) =>
        Assert.IsType<LaunchResolution.Resolved>(resolution).View;
}
