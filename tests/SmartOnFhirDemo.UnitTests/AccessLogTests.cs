using Microsoft.EntityFrameworkCore;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The access log's two claims: that a row survives the trip through SQLite intact, and
/// that the key it is filed under names an EHR rather than a launch.
/// </summary>
public class AccessLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static AccessLogEntry Read(string patientId = "pat-1") =>
        new(
            Noon,
            "launch-1",
            "https://ehr.example:443",
            patientId,
            "Practitioner/prac-1",
            "Patient",
            $"Patient/{patientId}",
            AccessOutcome.Ok,
            200
        );

    [Fact]
    public async Task A_recorded_read_comes_back_saying_what_it_said()
    {
        using var fixture = new AccessLogFixture();

        await fixture.Log.RecordAsync(Read(), TestContext.Current.CancellationToken);

        var stored = await fixture
            .Db.Entries.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Noon, stored.OccurredAt);
        Assert.Equal("launch-1", stored.LaunchId);
        Assert.Equal("https://ehr.example:443", stored.IssuerOrigin);
        Assert.Equal("pat-1", stored.PatientId);
        Assert.Equal("Practitioner/prac-1", stored.FhirUser);
        Assert.Equal("Patient", stored.ResourceType);
        Assert.Equal("Patient/pat-1", stored.RequestPath);
        Assert.Equal(AccessOutcome.Ok, stored.Outcome);
        Assert.Equal(200, stored.Status);
    }

    [Fact]
    public async Task The_database_assigns_the_id_the_caller_left_alone()
    {
        using var fixture = new AccessLogFixture();

        await fixture.Log.RecordAsync(Read("pat-1"), TestContext.Current.CancellationToken);
        await fixture.Log.RecordAsync(Read("pat-2"), TestContext.Current.CancellationToken);

        var ids = await fixture
            .Db.Entries.AsNoTracking()
            .Select(entry => entry.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, ids.Distinct().Count());
        Assert.DoesNotContain(0, ids);
    }

    // ---- What a row is filed under ----------------------------------------

    /// <summary>
    /// The SMART App Launcher's issuer, whose path segment base64-encodes the launch
    /// settings — the selected patient among them. Two launches against one launcher
    /// therefore differ in the raw string.
    /// </summary>
    private static string LauncherIssuer(string simulation) =>
        $"http://localhost:8080/v/r4/sim/{simulation}/fhir";

    [Fact]
    public void Two_launches_against_one_launcher_are_filed_under_one_ehr()
    {
        // Keyed on the raw iss, every patient would look like a different EHR and the
        // log could never answer "who has been in this chart".
        Assert.Equal(
            Smart.Origin(LauncherIssuer("WzAsInBhdC0xIl0")),
            Smart.Origin(LauncherIssuer("WzAsInBhdC0yIl0"))
        );
    }

    [Fact]
    public void Two_launchers_on_different_hosts_stay_apart()
    {
        Assert.NotEqual(
            Smart.Origin("https://launch.smarthealthit.org/v/r4/fhir"),
            Smart.Origin("http://localhost:8080/v/r4/fhir")
        );
    }

    [Fact]
    public void A_reference_that_is_not_an_absolute_http_url_is_no_key_at_all()
    {
        Assert.Null(Smart.Origin("Patient/pat-1"));
    }

    // ---- What a launch is allowed to read back ----------------------------

    private static AccessLogEntry Read(string launchId, string patientId) =>
        Read(patientId) with
        {
            LaunchId = launchId,
        };

    [Fact]
    public async Task A_launch_is_shown_the_requests_it_caused_and_nobody_elses()
    {
        // The guarantee the LaunchId column was added for. Scoped by issuer and patient
        // instead, this would hand one clinician the times and panels another had read
        // from the same chart — from a page that never asked whether they may know.
        using var fixture = new AccessLogFixture();

        await fixture.Log.RecordAsync(Read("mine", "pat-1"), TestContext.Current.CancellationToken);
        await fixture.Log.RecordAsync(
            Read("someone-elses", "pat-1"),
            TestContext.Current.CancellationToken
        );

        var rows = await fixture.Reader.ForLaunchAsync(
            "mine",
            limit: 10,
            TestContext.Current.CancellationToken
        );

        Assert.All(rows, row => Assert.Equal("mine", row.LaunchId));
        Assert.Single(rows);
    }

    [Fact]
    public async Task Rows_written_before_launches_were_recorded_belong_to_no_launch()
    {
        // Pre-migration rows carry "", which is what they are: unattributed. A fallback
        // to issuer and patient for a launch with no rows is the leak above, disguised.
        using var fixture = new AccessLogFixture();

        await fixture.Log.RecordAsync(Read("", "pat-1"), TestContext.Current.CancellationToken);

        Assert.Empty(
            await fixture.Reader.ForLaunchAsync(
                "some-launch",
                limit: 10,
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task The_newest_request_comes_back_first()
    {
        using var fixture = new AccessLogFixture();

        foreach (var patientId in new[] { "first", "second", "third" })
            await fixture.Log.RecordAsync(
                Read("mine", patientId),
                TestContext.Current.CancellationToken
            );

        var rows = await fixture.Reader.ForLaunchAsync(
            "mine",
            limit: 10,
            TestContext.Current.CancellationToken
        );

        // Every row here shares a timestamp, which is not a contrivance: a launch's probe
        // and its first reads land in the same second. Insertion order breaks the tie.
        Assert.Equal(["third", "second", "first"], rows.Select(row => row.PatientId));
    }

    [Fact]
    public async Task No_more_rows_come_back_than_were_asked_for()
    {
        using var fixture = new AccessLogFixture();

        for (var i = 0; i < 5; i++)
            await fixture.Log.RecordAsync(
                Read("mine", $"pat-{i}"),
                TestContext.Current.CancellationToken
            );

        var rows = await fixture.Reader.ForLaunchAsync(
            "mine",
            limit: 2,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, rows.Count);
    }
}
