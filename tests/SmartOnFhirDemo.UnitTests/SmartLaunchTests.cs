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
            clients,
            Options.Create(new SmartOptions { TrustedIssuers = [Iss] }),
            new Jwks(clients, new MemoryCache(new MemoryCacheOptions()), NullLogger<Jwks>.Instance),
            _accessLog.Log,
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
