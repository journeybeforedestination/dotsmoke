namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The wording of the section a launch is shown, and the one thing it must never say more
/// than it was given: rows past the cap.
/// </summary>
public class AccessViewTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly LaunchFacts Facts = new(
        "launch-1",
        "https://ehr.example:443",
        "pat-1",
        "Practitioner/prac-1",
        Noon.AddMinutes(5)
    );

    private static AccessLogEntry Entry(
        string path,
        string resourceType = "Patient",
        string outcome = AccessOutcome.Ok,
        int? status = 200
    ) =>
        new(
            Noon,
            "launch-1",
            "https://ehr.example:443",
            "pat-1",
            "Practitioner/prac-1",
            resourceType,
            path,
            outcome,
            status
        );

    private static AccessLine Only(AccessLogEntry entry) =>
        Assert.Single(AccessView.Of(Facts, [entry]).Lines);

    [Fact]
    public void The_lead_in_says_which_ehr_and_who_was_signed_in()
    {
        // Both are constant across a launch, so they are said once rather than on every row.
        var view = AccessView.Of(Facts, [Entry("Patient/pat-1")]);

        Assert.Equal("https://ehr.example:443", view.IssuerOrigin);
        Assert.Equal("Practitioner/prac-1", view.FhirUser);
    }

    [Fact]
    public void A_row_keeps_the_fhir_request_beside_the_sentence_about_it()
    {
        // Prose alone reads better and teaches nothing: the request is the lesson.
        var line = Only(Entry("Patient/pat-1"));

        Assert.Equal("Patient/pat-1", line.Request);
        Assert.Equal("A read of Patient", line.Asked);
        Assert.Equal("the EHR answered (200)", line.Answer);
        Assert.Equal("12:00:00", line.At);
    }

    [Fact]
    public void A_query_string_is_a_search_rather_than_a_read()
    {
        Assert.Equal(
            "A search for Condition",
            Only(Entry("Condition?patient=pat-1", resourceType: "Condition")).Asked
        );
    }

    [Fact]
    public void The_probe_the_launch_opened_with_is_named_for_what_it_asked()
    {
        // "A read of metadata" is true and says nothing; this is the first row every
        // launch has, and it is worth a reader knowing what it was for.
        // As Firely actually asks for it, which is why this matches on the resource type:
        // the query string would otherwise make the probe read as a search.
        Assert.Equal(
            "The server's capability statement",
            Only(Entry("metadata?_summary=true", resourceType: "metadata")).Asked
        );
    }

    [Theory]
    [InlineData(AccessOutcome.Denied, 403, "the EHR would not authorize it (403)")]
    [InlineData(AccessOutcome.NotFound, 404, "the EHR had no such record (404)")]
    [InlineData(AccessOutcome.Failed, 500, "the EHR could not answer (500)")]
    public void Every_answer_an_ehr_can_give_reads_as_a_sentence(
        string outcome,
        int status,
        string expected
    )
    {
        Assert.Equal(
            expected,
            Only(Entry("Patient/pat-1", outcome: outcome, status: status)).Answer
        );
    }

    [Fact]
    public void A_row_the_app_refused_says_nothing_was_sent()
    {
        // Status is null exactly when the app held the request back, and a rendered 0
        // would read as something the EHR said.
        var line = Only(
            Entry("Patient/someone-else", outcome: AccessOutcome.LaunchMismatch, status: null)
        );

        Assert.Equal(
            "nothing was sent — the page claimed a patient this launch does not name",
            line.Answer
        );
        Assert.DoesNotContain("(", line.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_that_was_cut_says_so_rather_than_just_ending()
    {
        // The caller reads one row past the cap for exactly this. A section that silently
        // stopped at fifty would misreport a busy launch as a quiet one.
        var entries = Enumerable
            .Range(0, AccessView.Rows + 1)
            .Select(_ => Entry("Patient/pat-1"))
            .ToList();

        var view = AccessView.Of(Facts, entries);

        Assert.Equal(AccessView.Rows, view.Lines.Count);
        Assert.True(view.Truncated);
    }

    [Fact]
    public void A_list_that_fits_does_not()
    {
        var entries = Enumerable
            .Range(0, AccessView.Rows)
            .Select(_ => Entry("Patient/pat-1"))
            .ToList();

        Assert.False(AccessView.Of(Facts, entries).Truncated);
    }
}
