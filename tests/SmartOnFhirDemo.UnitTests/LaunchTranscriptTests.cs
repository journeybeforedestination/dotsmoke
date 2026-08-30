using Microsoft.AspNetCore.WebUtilities;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The narrated launch renders whatever the transcript puts in front of it, so what the
/// transcript refuses to put there is the whole of the guarantee. These are mostly about
/// what is absent.
/// </summary>
public class LaunchTranscriptTests
{
    private const string Iss = "https://ehr.example/r4/fhir";
    private const string RedirectUri = "http://localhost:5000/learn/callback";

    /// <summary>Long enough to be abbreviated, distinctive enough to find if it is not.</summary>
    private const string Launch = "launch-handle-0123456789-abcdefghij-klmnopqrst-uvwxyz";
    private const string State = "state-0123456789-abcdefghij-klmnopqrst-uvwxyz";
    private const string Challenge = "challenge-0123456789-abcdefghij-klmnopqrst-uvwxyz";
    private const string Code = "code-0123456789-abcdefghij-klmnopqrst-uvwxyz";

    /// <summary>The one value in a launch that must never be rendered anywhere at all.</summary>
    private const string Verifier = "verifier-THIS-MUST-NEVER-APPEAR-IN-ANY-RENDERED-STEP";

    private const string AccessToken = "access-token-THIS-MUST-NEVER-APPEAR-EITHER";

    // ---- On the way out ---------------------------------------------------

    [Fact]
    public void Every_parameter_the_app_sends_to_the_ehr_is_explained()
    {
        // The point of the page is that nothing is sent unexplained, so a parameter added
        // to BuildAuthorizeUrl without a sentence about it should fail here.
        var prepared = Prepared();
        var explained = Steps(prepared).SelectMany(s => s.Fields).Select(f => f.Label).ToHashSet();

        var sent = QueryHelpers.ParseQuery(new Uri(prepared.AuthorizeUrl).Query).Keys;

        Assert.NotEmpty(sent);
        Assert.All(sent, parameter => Assert.Contains(parameter, explained));
    }

    [Fact]
    public void No_value_is_shown_without_a_sentence_saying_what_it_is_for()
    {
        var fields = Steps(Prepared()).SelectMany(s => s.Fields);

        Assert.All(fields, field => Assert.NotEqual("", field.Note));
    }

    [Fact]
    public void Opaque_values_are_abbreviated_rather_than_reproduced()
    {
        var values = Steps(Prepared()).SelectMany(s => s.Fields).Select(f => f.Value).ToList();

        Assert.DoesNotContain(values, value => value.Contains(Launch));
        Assert.DoesNotContain(values, value => value.Contains(Challenge));
        Assert.DoesNotContain(values, value => value.Contains(State));
    }

    [Fact]
    public void The_pkce_verifier_reaches_no_step_on_the_way_out()
    {
        // BeforeTheRedirect is handed the LaunchState, which holds the verifier.
        Assert.All(Steps(Prepared()), step => AssertAbsent(Verifier, step));
    }

    // ---- On the way back --------------------------------------------------

    [Fact]
    public void The_pause_before_the_exchange_withholds_the_verifier_it_was_handed()
    {
        var step = LaunchTranscript.TheCodeCameBack(Code, State, Session);

        AssertAbsent(Verifier, step);

        // Not merely omitted — named as withheld, because that is the lesson.
        Assert.Contains(Smart.Withheld, step.Payload);
    }

    [Fact]
    public void The_pause_before_the_exchange_abbreviates_the_live_code()
    {
        var step = LaunchTranscript.TheCodeCameBack(Code, State, Session);

        var code = Assert.Single(step.Fields, f => f.Label == "code");
        Assert.DoesNotContain(Code, code.Value);
        Assert.Contains(Code[..8], code.Value);
    }

    [Fact]
    public void The_token_step_withholds_the_token_and_reports_everything_else()
    {
        var step = LaunchTranscript.TheTokenResponse(Completed());

        AssertAbsent(AccessToken, step);
        Assert.Equal(
            Smart.Withheld,
            Assert.Single(step.Fields, f => f.Label == "access_token").Value
        );

        Assert.Equal("Bearer", Assert.Single(step.Fields, f => f.Label == "token_type").Value);
        Assert.Contains("3600", Assert.Single(step.Fields, f => f.Label == "expires_in").Value);
        Assert.Equal(
            "launch openid fhirUser patient/Patient.read",
            Assert.Single(step.Fields, f => f.Label == "scope").Value
        );
        Assert.Equal("pat-1", Assert.Single(step.Fields, f => f.Label == "patient").Value);
    }

    [Fact]
    public void The_patient_step_withholds_the_bearer_credential()
    {
        var step = LaunchTranscript.ThePatientRead(Completed());

        AssertAbsent(AccessToken, step);
        Assert.Contains(
            Smart.Withheld,
            Assert.Single(step.Fields, f => f.Label == "Authorization").Value
        );
        Assert.Equal(
            $"{Iss}/Patient/pat-1",
            Assert.Single(step.Fields, f => f.Label == "GET").Value
        );
    }

    // ---- Plumbing ---------------------------------------------------------

    /// <summary>Nowhere in the step: not a value, not a note, not the raw payload.</summary>
    private static void AssertAbsent(string secret, LaunchStep step)
    {
        Assert.All(
            step.Fields,
            field =>
            {
                Assert.DoesNotContain(secret, field.Value);
                Assert.DoesNotContain(secret, field.Note);
            }
        );

        Assert.DoesNotContain(secret, step.Payload ?? "");
    }

    private static IReadOnlyList<LaunchStep> Steps(LaunchOutcome.Prepared prepared) =>
        LaunchTranscript.BeforeTheRedirect(prepared);

    private static readonly SmartConfiguration Configuration = new(
        "https://ehr.example/authorize",
        "https://ehr.example/token"
    );

    private const string ConfigurationJson = """
        {"authorization_endpoint":"https://ehr.example/authorize","token_endpoint":"https://ehr.example/token"}
        """;

    private static readonly LaunchState Session = new(
        Iss,
        Configuration.TokenEndpoint,
        Verifier,
        RedirectUri
    );

    /// <summary>A prepared launch with a real authorize URL, built the way BeginAsync builds one.</summary>
    private static LaunchOutcome.Prepared Prepared() =>
        new(
            Smart.BuildAuthorizeUrl(
                Configuration,
                new SmartOptions(),
                RedirectUri,
                Iss,
                Launch,
                State,
                Challenge
            ),
            State,
            Session,
            $"{Iss}/.well-known/smart-configuration",
            ConfigurationJson
        );

    private static readonly IdTokenFacts Claims = new(
        Iss,
        "smart-on-fhir-demo",
        "opaque-subject-that-is-long-enough-to-abbreviate",
        "Practitioner/prac-1",
        new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 30, 13, 0, 0, TimeSpan.Zero)
    );

    private static CallbackOutcome.Completed Completed(
        IdTokenFacts? identity = null,
        string? identityUnavailable = null,
        LaunchUser? user = null,
        string? userUnavailable = null
    ) =>
        new(
            new PatientSummary("Alex Rivera", "Female", null, null, null, null, null),
            """{"resourceType":"Patient","id":"pat-1"}""",
            new TokenFacts(
                "Bearer",
                3600,
                "launch openid fhirUser patient/Patient.read",
                "pat-1",
                null,
                NeedPatientBanner: true,
                SmartStyleUrl: "https://ehr.example/smart-style.json"
            ),
            $$"""{"access_token":"{{Smart.Withheld}}","token_type":"Bearer","patient":"pat-1"}""",
            $"{Iss}/Patient/pat-1",
            identity,
            identityUnavailable,
            user,
            userUnavailable
        );

    private static CallbackOutcome.Completed Identified() =>
        Completed(Claims, user: new LaunchUser("Practitioner", "Dr. Albertine Orn", "93370"));

    // ---- Who launched it --------------------------------------------------

    [Fact]
    public void The_identity_step_withholds_the_raw_id_token()
    {
        var step = LaunchTranscript.WhoLaunchedThis(Identified());

        Assert.Contains("withheld", step.PayloadLabel);
        Assert.DoesNotContain(step.Fields, f => f.Label == "id_token" && f.Value != Smart.Withheld);
    }

    [Fact]
    public void Every_claim_the_app_checked_is_named_with_what_it_was_checked_against()
    {
        var step = LaunchTranscript.WhoLaunchedThis(Identified());

        foreach (var claim in new[] { "signature", "iss", "aud", "exp", "sub", "fhirUser" })
            Assert.Contains(step.Fields, f => f.Label == claim && f.Note.Length > 0);
    }

    [Fact]
    public void The_identity_step_says_why_validating_the_id_token_was_optional()
    {
        var step = LaunchTranscript.WhoLaunchedThis(Identified());

        Assert.Contains("3.1.3.7", step.Fields.Single(f => f.Label == "signature").Note);
    }

    [Fact]
    public void The_identity_step_admits_the_launcher_does_not_enforce_user_scopes()
    {
        var step = LaunchTranscript.WhoLaunchedThis(Identified());

        // Naming the scope without this would imply an enforcement that is not there.
        Assert.Contains(
            "does not actually enforce",
            step.Fields.Single(f => f.Label == "the user").Note
        );
    }

    [Fact]
    public void An_unproved_identity_names_nobody_and_says_why()
    {
        var step = LaunchTranscript.WhoLaunchedThis(
            Completed(identityUnavailable: "The EHR did not grant the openid scope.")
        );

        Assert.Contains("Nobody", step.Explanation);
        Assert.Contains("openid scope", step.Fields.Single(f => f.Label == "id_token").Note);
    }

    [Fact]
    public void A_claim_that_could_not_be_followed_still_reports_the_reason()
    {
        var step = LaunchTranscript.WhoLaunchedThis(
            Completed(Claims, userUnavailable: "The EHR would not return Practitioner/x (403).")
        );

        Assert.Contains("403", step.Fields.Single(f => f.Label == "the user").Note);
    }

    [Fact]
    public void The_token_step_reports_the_context_fields_the_ehr_returned()
    {
        var step = LaunchTranscript.TheTokenResponse(Identified());

        foreach (var field in new[] { "need_patient_banner", "smart_style_url" })
            Assert.Contains(step.Fields, f => f.Label == field);
    }

    [Fact]
    public void The_patient_read_is_the_seventh_step_now_that_identity_is_the_sixth()
    {
        Assert.StartsWith("6 · ", LaunchTranscript.WhoLaunchedThis(Identified()).Title);
        Assert.StartsWith("7 · ", LaunchTranscript.ThePatientRead(Identified()).Title);
    }
}
