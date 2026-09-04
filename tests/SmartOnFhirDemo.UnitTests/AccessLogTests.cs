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
}
