using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The launch decisions that a real EHR cannot easily be made to produce. Everything
/// reachable against a live launcher is covered by the integration tests instead.
/// </summary>
public class SmartLaunchTests : IDisposable
{
    /// <summary>
    /// A real access log on a real SQLite database, so what a launch writes can be read
    /// back rather than asserted against a fake that agrees with whatever it is told.
    /// </summary>
    private readonly AccessLogFixture _accessLog = new();

    public void Dispose()
    {
        _accessLog.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Iss = "https://ehr.example/r4/fhir";
    private const string RedirectUri = "http://localhost:5000/callback";

    private static readonly LaunchState Session = new(
        Iss,
        "https://ehr.example/token",
        "the-verifier",
        RedirectUri
    );

    // ---- Beginning a launch -----------------------------------------------

    [Theory]
    [InlineData(null, "launch-123")]
    [InlineData(Iss, null)]
    [InlineData("", "launch-123")]
    [InlineData("   ", "launch-123")]
    public async Task A_launch_without_both_parameters_goes_no_further(string? iss, string? launch)
    {
        var smart = Smart(_ =>
            throw new Xunit.Sdk.XunitException("Discovery should not have been attempted.")
        );

        Assert.IsType<LaunchOutcome.MissingParameters>(
            await smart.BeginAsync(iss, launch, RedirectUri, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task An_untrusted_issuer_is_refused_without_being_contacted()
    {
        var smart = Smart(_ =>
            throw new Xunit.Sdk.XunitException("The issuer must not be contacted.")
        );

        var outcome = Assert.IsType<LaunchOutcome.UntrustedIssuer>(
            await smart.BeginAsync(
                "https://evil.example/fhir",
                "launch-123",
                RedirectUri,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("https://evil.example/fhir", outcome.Iss);
    }

    [Fact]
    public async Task Discovery_asks_the_issuer_for_its_smart_configuration()
    {
        Uri? asked = null;
        var smart = Smart(request =>
        {
            asked = request.RequestUri;
            return Json(Configuration);
        });

        // The trailing slash must not produce a doubled one.
        await smart.BeginAsync(
            $"{Iss}/",
            "launch-123",
            RedirectUri,
            TestContext.Current.CancellationToken
        );

        Assert.Equal($"{Iss}/.well-known/smart-configuration", asked?.ToString());
    }

    [Fact]
    public async Task An_empty_smart_configuration_is_not_treated_as_a_launch()
    {
        var smart = Smart(_ => Json("null"));

        var outcome = Assert.IsType<LaunchOutcome.DiscoveryFailed>(
            await smart.BeginAsync(
                Iss,
                "launch-123",
                RedirectUri,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("empty", outcome.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, Configuration)]
    [InlineData(HttpStatusCode.NotFound, Configuration)]
    [InlineData(HttpStatusCode.OK, "this is not json")]
    public async Task Discovery_that_fails_or_returns_nonsense_is_reported(
        HttpStatusCode status,
        string body
    )
    {
        var smart = Smart(_ => Json(body, status));

        Assert.IsType<LaunchOutcome.DiscoveryFailed>(
            await smart.BeginAsync(
                Iss,
                "launch-123",
                RedirectUri,
                TestContext.Current.CancellationToken
            )
        );
    }

    [Theory]
    [InlineData(
        """{"authorization_endpoint":"https://elsewhere.example/authorize","token_endpoint":"https://ehr.example/token"}"""
    )]
    [InlineData(
        """{"authorization_endpoint":"https://ehr.example/authorize","token_endpoint":"https://elsewhere.example/token"}"""
    )]
    public async Task A_configuration_naming_endpoints_off_the_issuers_origin_starts_no_launch(
        string published
    )
    {
        // The allowlist trusts an origin, and whoever controls a path beneath it controls
        // this document — which is precisely what the launcher's simulation paths are. One
        // of these two is where the browser is sent, and the other is where the code goes.
        var smart = Smart(_ => Json(published));

        var outcome = Assert.IsType<LaunchOutcome.DiscoveryFailed>(
            await smart.BeginAsync(
                Iss,
                "launch-123",
                RedirectUri,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("another origin", outcome.Reason);
    }

    [Fact]
    public async Task A_configuration_missing_an_endpoint_says_which_one_rather_than_failing_later()
    {
        var smart = Smart(_ => Json("""{"token_endpoint":"https://ehr.example/token"}"""));

        var outcome = Assert.IsType<LaunchOutcome.DiscoveryFailed>(
            await smart.BeginAsync(
                Iss,
                "launch-123",
                RedirectUri,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("no authorization_endpoint", outcome.Reason);
    }

    [Fact]
    public async Task A_prepared_launch_carries_what_the_callback_will_need()
    {
        var smart = Smart(_ => Json(Configuration));

        var prepared = Assert.IsType<LaunchOutcome.Prepared>(
            await smart.BeginAsync(
                Iss,
                "launch-123",
                RedirectUri,
                TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith("https://ehr.example/authorize?", prepared.AuthorizeUrl);
        Assert.Contains($"state={prepared.State}", prepared.AuthorizeUrl);

        // The token endpoint comes from discovery, not from a guess about the issuer.
        Assert.Equal("https://ehr.example/token", prepared.Session.TokenEndpoint);
        Assert.Equal(Iss, prepared.Session.Iss);
        Assert.Equal(RedirectUri, prepared.Session.RedirectUri);
        Assert.NotEmpty(prepared.Session.CodeVerifier);
    }

    [Fact]
    public async Task Every_launch_gets_its_own_state_and_verifier()
    {
        var smart = Smart(_ => Json(Configuration));

        var first = Assert.IsType<LaunchOutcome.Prepared>(
            await smart.BeginAsync(Iss, "l", RedirectUri, TestContext.Current.CancellationToken)
        );
        var second = Assert.IsType<LaunchOutcome.Prepared>(
            await smart.BeginAsync(Iss, "l", RedirectUri, TestContext.Current.CancellationToken)
        );

        Assert.NotEqual(first.State, second.State);
        Assert.NotEqual(first.Session.CodeVerifier, second.Session.CodeVerifier);
    }

    // ---- Completing a launch ----------------------------------------------

    [Fact]
    public async Task An_error_from_the_ehr_is_reported_with_its_description()
    {
        var smart = Smart(_ => throw new Xunit.Sdk.XunitException("No token call should be made."));

        var outcome = Assert.IsType<CallbackOutcome.AuthorizationDenied>(
            (
                await smart.CompleteAsync(
                    null,
                    null,
                    "access_denied",
                    "The user said no",
                    Session,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        Assert.Equal("The user said no", outcome.Reason);
    }

    [Fact]
    public async Task An_error_without_a_description_falls_back_to_the_code()
    {
        var smart = Smart(_ => throw new Xunit.Sdk.XunitException("No token call should be made."));

        var outcome = Assert.IsType<CallbackOutcome.AuthorizationDenied>(
            (
                await smart.CompleteAsync(
                    null,
                    null,
                    "access_denied",
                    null,
                    Session,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        Assert.Equal("access_denied", outcome.Reason);
    }

    [Fact]
    public async Task A_callback_with_no_launch_in_flight_is_refused_before_the_token_call()
    {
        var smart = Smart(_ => throw new Xunit.Sdk.XunitException("No token call should be made."));

        Assert.IsType<CallbackOutcome.UnknownLaunch>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    null,
                    null,
                    launch: null,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );
    }

    [Fact]
    public async Task The_token_request_proves_possession_of_the_pkce_verifier()
    {
        string? body = null;
        var smart = Smart(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            // No patient context, so this stops right after the token exchange rather
            // than running on into a FHIR read this stub cannot answer.
            return Json("""{"access_token":"tok"}""");
        });

        await smart.CompleteAsync(
            "the-code",
            "the-state",
            null,
            null,
            Session,
            TestContext.Current.CancellationToken
        );

        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=the-code", body);
        Assert.Contains("code_verifier=the-verifier", body);
        Assert.Contains("client_id=smart-on-fhir-demo", body);
    }

    [Fact]
    public async Task A_rejected_token_exchange_keeps_the_status_and_the_reason()
    {
        var smart = Smart(_ => Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));

        var outcome = Assert.IsType<CallbackOutcome.TokenExchangeFailed>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    null,
                    null,
                    Session,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        Assert.Equal(400, outcome.Status);
        Assert.Contains("invalid_grant", outcome.Reason);
    }

    [Fact]
    public async Task A_token_endpoint_that_does_not_answer_is_a_sentence_not_an_exception()
    {
        // The clients carry a timeout, so this is a way a callback can end rather than a
        // way it can throw — and there is no status to report, because nothing answered.
        var smart = Smart(_ => throw new HttpRequestException("connection refused"));

        var outcome = Assert.IsType<CallbackOutcome.TokenEndpointUnreachable>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    null,
                    null,
                    Session,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        Assert.Contains("connection refused", outcome.Reason);
    }

    [Fact]
    public async Task A_token_response_without_an_access_token_is_not_trusted()
    {
        var smart = Smart(_ => Json("""{"patient":"pat-1"}"""));

        Assert.IsType<CallbackOutcome.NoAccessToken>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    null,
                    null,
                    Session,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );
    }

    [Fact]
    public async Task A_token_response_without_patient_context_stops_before_reading_anything()
    {
        var smart = Smart(_ => Json("""{"access_token":"tok"}"""));

        Assert.IsType<CallbackOutcome.NoPatientContext>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    null,
                    null,
                    Session,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );
    }

    // ---- What a finished launch is allowed to carry ------------------------
    //
    // The narrated launch renders these outcomes, so the guarantee that a page cannot
    // leak the access token is the guarantee that the token is not on them.

    [Fact]
    public async Task A_completed_launch_carries_what_the_token_said_but_not_the_token()
    {
        var smart = Completing(TokenJson);

        var completed = Assert.IsType<CallbackOutcome.Completed>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    null,
                    null,
                    Session,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        Assert.DoesNotContain(AccessToken, completed.TokenJson);
        Assert.Contains(SmartOnFhirDemo.Smart.Withheld, completed.TokenJson);
        Assert.DoesNotContain(AccessToken, completed.RawJson);

        Assert.Equal("Bearer", completed.Token.TokenType);
        Assert.Equal(3600, completed.Token.ExpiresIn);
        Assert.Equal("launch patient/Patient.read", completed.Token.Scope);
        Assert.Equal("pat-1", completed.Token.Patient);
        Assert.Equal($"{Iss}/Patient/pat-1", completed.PatientUrl);
    }

    [Fact]
    public async Task Every_credential_the_token_endpoint_returns_is_removed_not_just_the_access_token()
    {
        var smart = Completing(
            """
            {"access_token":"a-token","refresh_token":"a-refresh","id_token":"an-id","patient":"pat-1"}
            """
        );

        var completed = Assert.IsType<CallbackOutcome.Completed>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    null,
                    null,
                    Session,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        Assert.DoesNotContain("a-token", completed.TokenJson);
        Assert.DoesNotContain("a-refresh", completed.TokenJson);
        Assert.DoesNotContain("an-id", completed.TokenJson);
    }

    [Fact]
    public async Task The_access_token_is_still_presented_to_the_fhir_server()
    {
        // Withheld from the caller, not from the request it exists for.
        string? presented = null;
        var smart = Completing(
            TokenJson,
            request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/Patient/"))
                    presented = request.Headers.Authorization?.ToString();
            }
        );

        await smart.CompleteAsync(
            "the-code",
            "the-state",
            null,
            null,
            Session,
            TestContext.Current.CancellationToken
        );

        Assert.Equal($"Bearer {AccessToken}", presented);
    }

    [Fact]
    public async Task A_prepared_launch_keeps_the_configuration_it_discovered()
    {
        // The narrated launch shows the document the EHR published, not just the two
        // fields this app parses out of it.
        var smart = Smart(_ => Json(Configuration));

        var prepared = Assert.IsType<LaunchOutcome.Prepared>(
            await smart.BeginAsync(
                Iss,
                "launch-123",
                RedirectUri,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal($"{Iss}/.well-known/smart-configuration", prepared.WellKnownUrl);
        Assert.Contains("authorization_endpoint", prepared.ConfigurationJson);
    }

    // ---- Plumbing ---------------------------------------------------------

    private const string Configuration = """
        {"authorization_endpoint":"https://ehr.example/authorize","token_endpoint":"https://ehr.example/token"}
        """;

    // ---- Who launched it --------------------------------------------------

    /// <summary>A launch whose EHR publishes both the fields id_token validation needs.</summary>
    private static readonly LaunchState SsoSession = Session with
    {
        Issuer = TestIdTokens.Issuer,
        JwksUri = "https://ehr.example/keys",
    };

    private async Task<CallbackOutcome.Completed> CompleteSso(
        string tokenJson,
        LaunchState? session = null,
        string? jwksJson = null,
        string? patientJson = null
    )
    {
        var smart = Completing(
            tokenJson,
            jwks: jwksJson ?? TestIdTokens.JwksJson(TestIdTokens.Ehr),
            patientJson: patientJson
        );

        return Assert.IsType<CallbackOutcome.Completed>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    error: null,
                    errorDescription: null,
                    session ?? SsoSession,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );
    }

    private static string TokenJsonWith(string idToken, string patient = "pat-1") =>
        $$"""
            {"access_token":"the-access-token","token_type":"Bearer","expires_in":3600,
             "scope":"launch openid fhirUser patient/Patient.read","patient":"{{patient}}",
             "id_token":"{{idToken}}"}
            """;

    [Fact]
    public async Task A_validated_id_token_puts_the_launching_user_on_the_outcome()
    {
        var completed = await CompleteSso(TokenJsonWith(TestIdTokens.Token()));

        Assert.Null(completed.IdentityUnavailable);
        Assert.Equal(TestIdTokens.FhirUser, completed.Identity?.FhirUser);
    }

    [Fact]
    public async Task A_token_response_without_an_id_token_says_the_scope_was_not_granted()
    {
        var completed = await CompleteSso(TokenJson);

        Assert.Null(completed.Identity);
        Assert.Contains("openid scope", completed.IdentityUnavailable);
    }

    [Fact]
    public async Task An_issuer_publishing_no_jwks_uri_leaves_the_id_token_unvalidated_and_unshown()
    {
        var completed = await CompleteSso(TokenJsonWith(TestIdTokens.Token()), session: Session);

        Assert.Null(completed.Identity);
        Assert.Contains("nothing to validate the id_token against", completed.IdentityUnavailable);
    }

    [Fact]
    public async Task Keys_that_cannot_be_read_leave_the_identity_unavailable()
    {
        var completed = await CompleteSso(TokenJsonWith(TestIdTokens.Token()), jwksJson: "{}");

        Assert.Null(completed.Identity);
        Assert.Contains("could not be read", completed.IdentityUnavailable);
    }

    [Fact]
    public async Task An_id_token_that_fails_validation_is_reported_rather_than_believed()
    {
        var forged = TestIdTokens.Token(signedWith: TestIdTokens.Impostor);

        var completed = await CompleteSso(TokenJsonWith(forged));

        Assert.Null(completed.Identity);
        Assert.Contains("id_token was refused", completed.IdentityUnavailable);
    }

    [Fact]
    public async Task An_identity_that_cannot_be_established_still_leaves_the_patient_read()
    {
        // The whole point of degrading rather than failing: the summary is the app's job.
        var completed = await CompleteSso(
            TokenJsonWith(TestIdTokens.Token(signedWith: TestIdTokens.Impostor))
        );

        Assert.Equal("Alex Rivera", completed.Summary.Name);
    }

    [Fact]
    public async Task A_relative_fhirUser_reference_is_read_from_the_launch_server()
    {
        var completed = await CompleteSso(TokenJsonWith(TestIdTokens.Token()));

        // The launcher returns a relative reference even though SMART says it SHOULD be
        // absolute, so this is the shape that actually turns up in practice.
        Assert.Equal("Dr. Albertine Orn", completed.User?.Name);
        Assert.Equal("Practitioner", completed.User?.ResourceType);
    }

    [Fact]
    public async Task An_absolute_fhirUser_reference_on_the_same_server_is_followed()
    {
        var completed = await CompleteSso(
            TokenJsonWith(TestIdTokens.Token(fhirUser: $"{Iss}/Practitioner/prac-1"))
        );

        Assert.Equal("Dr. Albertine Orn", completed.User?.Name);
    }

    [Fact]
    public async Task An_absolute_fhirUser_reference_elsewhere_is_not_followed()
    {
        var asked = new List<string>();

        var smart = Completing(
            TokenJsonWith(TestIdTokens.Token(fhirUser: "https://elsewhere.example/Practitioner/x")),
            observe: request => asked.Add(request.RequestUri!.ToString()),
            jwks: TestIdTokens.JwksJson(TestIdTokens.Ehr)
        );

        var completed = Assert.IsType<CallbackOutcome.Completed>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    error: null,
                    errorDescription: null,
                    SsoSession,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        // Following it would hand this server's access token to a server the token was
        // never issued for, which is the whole reason the origin is checked.
        Assert.Null(completed.User);
        Assert.Contains("other than the one this launch is for", completed.UserUnavailable);
        Assert.DoesNotContain(asked, url => url.Contains("elsewhere.example"));
    }

    [Fact]
    public async Task Signing_keys_published_off_the_issuers_origin_are_not_fetched()
    {
        var asked = new List<string>();

        var smart = Completing(
            TokenJsonWith(TestIdTokens.Token()),
            observe: request => asked.Add(request.RequestUri!.ToString()),
            jwks: TestIdTokens.JwksJson(TestIdTokens.Ehr)
        );

        var completed = Assert.IsType<CallbackOutcome.Completed>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    error: null,
                    errorDescription: null,
                    SsoSession with
                    {
                        JwksUri = "https://elsewhere.example/keys",
                    },
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        // Whoever answers at a jwks_uri decides which id_tokens this app believes, so it
        // has to be the EHR the app trusts. Identity degrades rather than failing: the
        // launch stands, and the summary is still read.
        Assert.Null(completed.Identity);
        Assert.Contains("another origin", completed.IdentityUnavailable);
        Assert.DoesNotContain(asked, url => url.Contains("elsewhere.example"));
        Assert.Equal("Alex Rivera", completed.Summary.Name);
    }

    [Theory]
    // None of these is a well-formed absolute URL, and .NET says so — which is why asking
    // that question was the wrong guard. The first two resolve against the FHIR base to a
    // host that is not it; the third is not a reference this app can place at all.
    [InlineData("//elsewhere.example/Practitioner/prac-1")]
    [InlineData("https://elsewhere.example/a b/Practitioner/prac-1")]
    [InlineData("../../Practitioner/prac-1")]
    public async Task A_fhirUser_that_merely_fails_to_look_absolute_is_not_followed(string fhirUser)
    {
        var asked = new List<string>();

        var smart = Completing(
            TokenJsonWith(TestIdTokens.Token(fhirUser: fhirUser)),
            observe: request => asked.Add(request.RequestUri!.ToString()),
            jwks: TestIdTokens.JwksJson(TestIdTokens.Ehr)
        );

        var completed = Assert.IsType<CallbackOutcome.Completed>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    error: null,
                    errorDescription: null,
                    SsoSession,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        Assert.Null(completed.User);
        Assert.NotNull(completed.UserUnavailable);
        Assert.DoesNotContain(asked, url => url.Contains("Practitioner"));
    }

    [Fact]
    public async Task A_user_the_server_will_not_return_leaves_the_patient_summary_standing()
    {
        var smart = Smart(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/metadata", StringComparison.Ordinal) ? Fhir(CapabilityStatement)
                : path.Contains("/Practitioner/") ? Fhir(ForbiddenOutcome, HttpStatusCode.Forbidden)
                : path.Contains("/Patient/") ? Fhir(PatientJson)
                : path.EndsWith("/keys", StringComparison.Ordinal)
                    ? Json(TestIdTokens.JwksJson(TestIdTokens.Ehr))
                : Json(TokenJsonWith(TestIdTokens.Token()));
        });

        var completed = Assert.IsType<CallbackOutcome.Completed>(
            (
                await smart.CompleteAsync(
                    "the-code",
                    "the-state",
                    error: null,
                    errorDescription: null,
                    SsoSession,
                    TestContext.Current.CancellationToken
                )
            ).Outcome
        );

        // An EHR is not obliged to grant user/Practitioner.read just because it was asked.
        Assert.Null(completed.User);
        Assert.Contains("403", completed.UserUnavailable);
        Assert.Equal("Alex Rivera", completed.Summary.Name);
    }

    // ---- What the launch wrote down ---------------------------------------

    private async Task<IReadOnlyList<AccessLogEntry>> RowsAsync() =>
        await _accessLog
            .Db.Entries.AsNoTracking()
            .OrderBy(entry => entry.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reading_a_patient_writes_one_row_naming_that_patient()
    {
        await CompleteSso(TokenJsonWith(TestIdTokens.Token()));

        var read = Assert.Single(await RowsAsync(), entry => entry.RequestPath == "Patient/pat-1");

        Assert.Equal("Patient", read.ResourceType);
        Assert.Equal("pat-1", read.PatientId);
        Assert.Equal(TestIdTokens.FhirUser, read.FhirUser);
        Assert.Equal(AccessOutcome.Ok, read.Outcome);
        Assert.Equal(200, read.Status);

        // The key is the EHR, not the launch: the issuer's path carries the simulation.
        Assert.Equal(SmartOnFhirDemo.Smart.Origin(Iss), read.IssuerOrigin);
    }

    [Fact]
    public async Task Every_request_the_launch_makes_to_the_ehr_is_written_down()
    {
        await CompleteSso(TokenJsonWith(TestIdTokens.Token()));

        // Including the two the app makes without being asked to: the version check the
        // Firely client does first, and the read of whoever fhirUser named. A read the
        // page never shows is still a read. The query string is kept — with search
        // coming, what was asked for is not always in the path alone.
        Assert.Equal(
            ["Patient/pat-1", "Practitioner/prac-1", "metadata?_summary=true"],
            (await RowsAsync()).Select(entry => entry.RequestPath).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task A_read_the_ehr_refuses_is_written_down_as_refused()
    {
        var smart = Smart(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/metadata", StringComparison.Ordinal) ? Fhir(CapabilityStatement)
                : path.Contains("/Practitioner/") ? Fhir(ForbiddenOutcome, HttpStatusCode.Forbidden)
                : path.Contains("/Patient/") ? Fhir(PatientJson)
                : path.EndsWith("/keys", StringComparison.Ordinal)
                    ? Json(TestIdTokens.JwksJson(TestIdTokens.Ehr))
                : Json(TokenJsonWith(TestIdTokens.Token()));
        });

        await smart.CompleteAsync(
            "the-code",
            "the-state",
            error: null,
            errorDescription: null,
            SsoSession,
            TestContext.Current.CancellationToken
        );

        var refused = Assert.Single(
            await RowsAsync(),
            entry => entry.RequestPath == "Practitioner/prac-1"
        );

        Assert.Equal(AccessOutcome.Denied, refused.Outcome);
        Assert.Equal(403, refused.Status);
    }

    [Theory]
    // What an EHR's answer meant, kept apart from the code it said it with: a reader of
    // the log asks whether a read happened, not which 4xx an implementation chose.
    [InlineData(HttpStatusCode.Forbidden, AccessOutcome.Denied)]
    [InlineData(HttpStatusCode.Unauthorized, AccessOutcome.Denied)]
    [InlineData(HttpStatusCode.NotFound, AccessOutcome.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, AccessOutcome.Failed)]
    [InlineData(HttpStatusCode.BadGateway, AccessOutcome.Failed)]
    public async Task What_the_ehr_answered_is_recorded_as_what_it_meant(
        HttpStatusCode status,
        string outcome
    )
    {
        var smart = Smart(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/metadata", StringComparison.Ordinal) ? Fhir(CapabilityStatement)
                : path.Contains("/Patient/") ? Fhir(ForbiddenOutcome, status)
                : Json(TokenJson);
        });

        await smart.CompleteAsync(
            "the-code",
            "the-state",
            error: null,
            errorDescription: null,
            Session,
            TestContext.Current.CancellationToken
        );

        var read = Assert.Single(await RowsAsync(), entry => entry.RequestPath == "Patient/pat-1");

        Assert.Equal(outcome, read.Outcome);
        Assert.Equal((int)status, read.Status);
    }

    [Fact]
    public async Task Two_launches_sharing_one_pooled_handler_are_still_told_apart()
    {
        // The regression this design exists to prevent. StubClientFactory hands out the
        // same inner handler every time, as the real factory's two-minute pool does; a
        // handler that resolved "the current launch" from DI rather than being handed one
        // would file the second launch's read under the first launch's patient.
        await CompleteSso(TokenJsonWith(TestIdTokens.Token()));
        await CompleteSso(
            TokenJsonWith(TestIdTokens.Token(), patient: "pat-2"),
            patientJson: OtherPatientJson
        );

        var reads = (await RowsAsync())
            .Where(entry => entry.ResourceType == "Patient")
            .Select(entry => (entry.PatientId, entry.RequestPath))
            .ToList();

        Assert.Equal([("pat-1", "Patient/pat-1"), ("pat-2", "Patient/pat-2")], reads);
    }

    // ---- Reading on after the summary -------------------------------------

    private const string ConditionBundle = """
        {"resourceType":"Bundle","type":"searchset","entry":[
          {"resource":{"resourceType":"Condition","id":"c1",
            "clinicalStatus":{"coding":[{"code":"active","display":"Active"}]},
            "code":{"text":"Essential hypertension"}}},
          {"resource":{"resourceType":"Condition","id":"c2",
            "code":{"coding":[{"code":"44054006","display":"Type 2 diabetes mellitus"}]}}}]}
        """;

    /// <summary>A launch already established, so a panel has something to read against.</summary>
    private async Task<(Chart Chart, IMemoryCache Cache, LaunchContext Context)> EstablishedAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond
    )
    {
        var smart = Completing(TokenJson);

        var result = await smart.CompleteAsync(
            "the-code",
            "the-state",
            error: null,
            errorDescription: null,
            Session,
            TestContext.Current.CancellationToken
        );

        var context = result.Context!;
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.RememberLaunch(
            "one-browser",
            context,
            (CallbackOutcome.Completed)result.Outcome,
            TestIdTokens.Clock
        );

        var clients = new StubClientFactory(new StubHandler(respond));

        return (
            new Chart(
                cache,
                new FhirClients(clients, _accessLog.Log, TestIdTokens.Clock),
                TestIdTokens.Clock,
                NullLogger<Chart>.Instance
            ),
            cache,
            context
        );
    }

    private async Task<ChartOutcome> PanelAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        ChartPanel? panel = null
    )
    {
        var (chart, _, context) = await EstablishedAsync(respond);

        return await chart.ReadAsync(
            "one-browser",
            context.LaunchId,
            context.PatientId,
            panel ?? ChartPanel.Conditions,
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task A_panel_reads_the_search_the_launch_is_scoped_to()
    {
        string? asked = null;

        var read = Assert.IsType<ChartOutcome.Read>(
            await PanelAsync(request =>
            {
                asked = request.RequestUri!.PathAndQuery;
                return Fhir(ConditionBundle);
            })
        );

        // Searched for this patient, not read by id: the token authorises a class of data.
        Assert.Contains("Condition", asked);
        Assert.Contains("patient=pat-1", asked);

        // The EHR's own wording where it gave one, and the coding's display where it did
        // not. Neither is a code this app maps.
        Assert.Equal(["Essential hypertension — Active", "Type 2 diabetes mellitus"], read.Entries);
    }

    [Fact]
    public async Task A_vitals_panel_asks_for_the_category_and_not_every_observation()
    {
        string? asked = null;

        await PanelAsync(
            request =>
            {
                asked = request.RequestUri!.PathAndQuery;
                return Fhir("""{"resourceType":"Bundle","type":"searchset"}""");
            },
            ChartPanel.Vitals
        );

        Assert.Contains("category=vital-signs", asked);
    }

    [Fact]
    public async Task A_patient_with_none_of_something_is_said_so_rather_than_shown_nothing()
    {
        var outcome = await PanelAsync(_ =>
            Fhir("""{"resourceType":"Bundle","type":"searchset"}""")
        );

        // An empty list and a refusal look identical on screen and mean opposite things.
        Assert.IsType<ChartOutcome.Empty>(outcome);
        Assert.Contains("no conditions recorded", LaunchMessages.For(outcome));
    }

    [Fact]
    public async Task A_scope_the_ehr_did_not_grant_is_a_sentence_not_a_failure()
    {
        var outcome = Assert.IsType<ChartOutcome.Denied>(
            await PanelAsync(_ => Fhir(ForbiddenOutcome, HttpStatusCode.Forbidden))
        );

        Assert.Equal(403, outcome.Status);
        Assert.Contains("does not oblige an EHR to grant it", LaunchMessages.For(outcome));
    }

    [Fact]
    public async Task A_panel_is_written_to_the_access_log_like_any_other_read()
    {
        await PanelAsync(_ => Fhir(ConditionBundle));

        var search = Assert.Single(await RowsAsync(), entry => entry.ResourceType == "Condition");

        Assert.Equal("pat-1", search.PatientId);
        Assert.Equal(AccessOutcome.Ok, search.Outcome);
    }

    /// <summary>
    /// One of each shape an Observation's value comes in, plus one with no value at all.
    /// FHIR makes every one of these legal for a vital sign, and a formatter that handles
    /// only Quantity renders a blank line against a real server.
    /// </summary>
    private const string ObservationBundle = """
        {"resourceType":"Bundle","type":"searchset","entry":[
          {"resource":{"resourceType":"Observation","id":"o1","status":"final",
            "code":{"text":"Body weight"},
            "valueQuantity":{"value":72.5,"unit":"kg"}}},
          {"resource":{"resourceType":"Observation","id":"o2","status":"final",
            "code":{"text":"Heart rate"},
            "valueQuantity":{"value":68}}},
          {"resource":{"resourceType":"Observation","id":"o3","status":"final",
            "code":{"text":"Smoking status"},
            "valueCodeableConcept":{"text":"Never smoked"}}},
          {"resource":{"resourceType":"Observation","id":"o4","status":"final",
            "code":{"text":"Notes"},
            "valueString":"within normal limits"}},
          {"resource":{"resourceType":"Observation","id":"o5","status":"final",
            "code":{"text":"Blood pressure"}}}]}
        """;

    [Fact]
    public async Task A_vital_sign_reads_as_its_name_and_whatever_shape_its_value_came_in()
    {
        var read = Assert.IsType<ChartOutcome.Read>(
            await PanelAsync(_ => Fhir(ObservationBundle), ChartPanel.Vitals)
        );

        Assert.Equal(
            [
                "Body weight — 72.5 kg",
                "Heart rate — 68",
                "Smoking status — Never smoked",
                "Notes — within normal limits",
                // A vital sign the EHR recorded without a value is still a row: saying the
                // measurement was taken and left blank beats dropping it silently.
                "Blood pressure",
            ],
            read.Entries
        );
    }

    /// <summary>
    /// R4's <c>medication[x]</c> is a choice, and both arms turn up in practice: the
    /// launcher's sandbox inlines a CodeableConcept, plenty of real servers reference a
    /// Medication resource instead.
    /// </summary>
    private const string MedicationBundle = """
        {"resourceType":"Bundle","type":"searchset","entry":[
          {"resource":{"resourceType":"MedicationRequest","id":"m1","status":"active",
            "intent":"order","subject":{"reference":"Patient/pat-1"},
            "medicationCodeableConcept":{"text":"Lisinopril 10mg tablet"}}},
          {"resource":{"resourceType":"MedicationRequest","id":"m2","status":"stopped",
            "intent":"order","subject":{"reference":"Patient/pat-1"},
            "medicationReference":{"reference":"Medication/med-1",
              "display":"Metformin 500mg tablet"}}},
          {"resource":{"resourceType":"MedicationRequest","id":"m3","status":"completed",
            "intent":"order","subject":{"reference":"Patient/pat-1"},
            "medicationReference":{"reference":"Medication/med-2"}}},
          {"resource":{"resourceType":"MedicationRequest","id":"m4","status":"draft",
            "intent":"order","subject":{"reference":"Patient/pat-1"}}}]}
        """;

    [Fact]
    public async Task A_medication_reads_whether_it_is_named_inline_or_referenced()
    {
        var read = Assert.IsType<ChartOutcome.Read>(
            await PanelAsync(_ => Fhir(MedicationBundle), ChartPanel.Medications)
        );

        Assert.Equal(
            [
                "Lisinopril 10mg tablet — active",
                "Metformin 500mg tablet — stopped",
                // A reference with no display is the reference: a link the reader can
                // follow beats "(unnamed)", and this app does not chase it.
                "Medication/med-2 — completed",
                // medication[x] is required in R4, so this resource is invalid — which is
                // not the same as absent, and servers emit it. The status is still true.
                "draft",
            ],
            read.Entries
        );
    }

    [Fact]
    public async Task A_resource_the_search_did_not_ask_for_still_reads_as_something()
    {
        // Servers put things in a searchset that were not searched for — an
        // OperationOutcome carrying a warning, an _include'd resource. Rendering a blank
        // line for those would look like data that failed to load.
        var read = Assert.IsType<ChartOutcome.Read>(
            await PanelAsync(_ =>
                Fhir(
                    """
                    {"resourceType":"Bundle","type":"searchset","entry":[
                      {"resource":{"resourceType":"OperationOutcome",
                        "issue":[{"severity":"warning","code":"too-costly"}]}}]}
                    """
                )
            )
        );

        Assert.Equal(["OperationOutcome"], read.Entries);
    }

    [Theory]
    // No code, but a status: the status is all there is to say.
    [InlineData(
        """{"resourceType":"Condition","clinicalStatus":{"coding":[{"code":"active"}]}}""",
        "active"
    )]
    // Neither. A row that says nothing still says one was there.
    [InlineData("""{"resourceType":"Condition"}""", "(unnamed)")]
    // No text on the code, so the coding's display; and with no display, the bare code.
    [InlineData(
        """{"resourceType":"Condition","code":{"coding":[{"code":"73211009"}]}}""",
        "73211009"
    )]
    public async Task A_condition_the_ehr_barely_described_still_reads_as_a_row(
        string condition,
        string expected
    )
    {
        var read = Assert.IsType<ChartOutcome.Read>(
            await PanelAsync(_ =>
                Fhir(
                    $$"""
                    {"resourceType":"Bundle","type":"searchset",
                     "entry":[{"resource":{{condition}}}]}
                    """
                )
            )
        );

        Assert.Equal([expected], read.Entries);
    }

    [Fact]
    public async Task An_ehr_that_breaks_on_a_panel_says_so_rather_than_calling_it_refused()
    {
        // A 500 is not a scope decision, and telling the reader their access was denied
        // would send them to argue with the wrong person.
        var outcome = Assert.IsType<ChartOutcome.Unavailable>(
            await PanelAsync(_ =>
                Fhir(
                    """
                    {"resourceType":"OperationOutcome","issue":[
                      {"severity":"error","code":"exception",
                       "details":{"text":"Index out of range"}}]}
                    """,
                    HttpStatusCode.InternalServerError
                )
            )
        );

        Assert.Equal(500, outcome.Status);
        Assert.Contains("Index out of range", outcome.Reason);
        Assert.Contains("The EHR returned 500", LaunchMessages.For(outcome));
    }

    [Fact]
    public async Task A_panel_asked_for_by_a_browser_that_did_not_launch_it_reads_nothing()
    {
        var (chart, _, context) = await EstablishedAsync(_ =>
            throw new Xunit.Sdk.XunitException("No search should be made.")
        );

        Assert.IsType<ChartOutcome.LaunchGone>(
            await chart.ReadAsync(
                "another-browser",
                context.LaunchId,
                context.PatientId,
                ChartPanel.Conditions,
                TestContext.Current.CancellationToken
            )
        );
    }

    // ---- Fixtures ---------------------------------------------------------

    private const string AccessToken = "the-access-token";

    private const string TokenJson = """
        {"access_token":"the-access-token","token_type":"Bearer","expires_in":3600,
         "scope":"launch patient/Patient.read","patient":"pat-1"}
        """;

    /// <summary>Enough of a CapabilityStatement for FhirClientSettings.VerifyFhirVersion.</summary>
    private const string CapabilityStatement = """
        {"resourceType":"CapabilityStatement","status":"active","date":"2024-01-01",
         "kind":"instance","fhirVersion":"4.0.1","format":["json"]}
        """;

    private const string PractitionerJson = """
        {"resourceType":"Practitioner","id":"prac-1",
         "name":[{"family":"Orn","given":["Albertine"],"prefix":["Dr."]}]}
        """;

    private const string PatientJson = """
        {"resourceType":"Patient","id":"pat-1","gender":"female",
         "name":[{"family":"Rivera","given":["Alex"]}]}
        """;

    private const string OtherPatientJson = """
        {"resourceType":"Patient","id":"pat-2","gender":"male",
         "name":[{"family":"Nakamura","given":["Jun"]}]}
        """;

    /// <summary>
    /// A launch that runs all the way through: the token exchange, the version check the
    /// Firely client makes first, and then the patient read.
    /// </summary>
    private SmartLaunch Completing(
        string tokenJson,
        Action<HttpRequestMessage>? observe = null,
        string? jwks = null,
        string? patientJson = null
    ) =>
        Smart(request =>
        {
            observe?.Invoke(request);

            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/metadata", StringComparison.Ordinal) ? Fhir(CapabilityStatement)
                : path.Contains("/Patient/") ? Fhir(patientJson ?? PatientJson)
                : path.Contains("/Practitioner/") ? Fhir(PractitionerJson)
                : path.EndsWith("/keys", StringComparison.Ordinal) ? Json(jwks ?? "{}")
                : Json(tokenJson);
        });

    private const string ForbiddenOutcome = """
        {"resourceType":"OperationOutcome","issue":[
          {"severity":"error","code":"forbidden","details":{"text":"Scope not granted"}}]}
        """;

    private static HttpResponseMessage Fhir(
        string body,
        HttpStatusCode status = HttpStatusCode.OK
    ) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/fhir+json") };

    private SmartLaunch Smart(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        // One stub serves discovery, the token endpoint, the JWKS and FHIR alike, so a test
        // decides what the EHR is by what it answers to, not by wiring.
        var clients = new StubClientFactory(new StubHandler(respond));

        return new SmartLaunch(
            clients,
            new FhirClients(clients, _accessLog.Log, TestIdTokens.Clock),
            Options.Create(new SmartOptions { TrustedIssuers = [Iss] }),
            new Jwks(clients, new MemoryCache(new MemoryCacheOptions()), NullLogger<Jwks>.Instance),
            TestIdTokens.Clock,
            NullLogger<SmartLaunch>.Instance
        );
    }

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode status = HttpStatusCode.OK
    ) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => Task.FromResult(respond(request));
    }

    /// <summary>
    /// Hands out the same handler every time, which is what IHttpClientFactory's pooling
    /// does and what makes the misattribution this design guards against reproducible: two
    /// launches down one inner handler have to still be told apart.
    /// </summary>
    private sealed class StubClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory,
            IHttpMessageHandlerFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

        public HttpMessageHandler CreateHandler(string name) => handler;
    }
}
