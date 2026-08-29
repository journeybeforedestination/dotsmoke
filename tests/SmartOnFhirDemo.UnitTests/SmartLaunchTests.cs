using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SmartOnFhirDemo.UnitTests;

/// <summary>
/// The launch decisions that a real EHR cannot easily be made to produce. Everything
/// reachable against a live launcher is covered by the integration tests instead.
/// </summary>
public class SmartLaunchTests
{
    private const string Iss = "https://ehr.example/r4/fhir";
    private const string RedirectUri = "http://localhost:5000/callback";

    private static readonly LaunchState Session =
        new(Iss, "https://ehr.example/token", "the-verifier", RedirectUri);

    // ---- Beginning a launch -----------------------------------------------

    [Theory]
    [InlineData(null, "launch-123")]
    [InlineData(Iss, null)]
    [InlineData("", "launch-123")]
    [InlineData("   ", "launch-123")]
    public async Task A_launch_without_both_parameters_goes_no_further(string? iss, string? launch)
    {
        var smart = Smart(_ => throw new Xunit.Sdk.XunitException("Discovery should not have been attempted."));

        Assert.IsType<LaunchOutcome.MissingParameters>(await smart.BeginAsync(iss, launch, RedirectUri, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_untrusted_issuer_is_refused_without_being_contacted()
    {
        var smart = Smart(_ => throw new Xunit.Sdk.XunitException("The issuer must not be contacted."));

        var outcome = Assert.IsType<LaunchOutcome.UntrustedIssuer>(
            await smart.BeginAsync(
                "https://evil.example/fhir", "launch-123", RedirectUri, TestContext.Current.CancellationToken));

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
        await smart.BeginAsync($"{Iss}/", "launch-123", RedirectUri, TestContext.Current.CancellationToken);

        Assert.Equal($"{Iss}/.well-known/smart-configuration", asked?.ToString());
    }

    [Fact]
    public async Task An_empty_smart_configuration_is_not_treated_as_a_launch()
    {
        var smart = Smart(_ => Json("null"));

        var outcome = Assert.IsType<LaunchOutcome.DiscoveryFailed>(
            await smart.BeginAsync(Iss, "launch-123", RedirectUri, TestContext.Current.CancellationToken));

        Assert.Contains("empty", outcome.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, Configuration)]
    [InlineData(HttpStatusCode.NotFound, Configuration)]
    [InlineData(HttpStatusCode.OK, "this is not json")]
    public async Task Discovery_that_fails_or_returns_nonsense_is_reported(HttpStatusCode status, string body)
    {
        var smart = Smart(_ => Json(body, status));

        Assert.IsType<LaunchOutcome.DiscoveryFailed>(
            await smart.BeginAsync(Iss, "launch-123", RedirectUri, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_prepared_launch_carries_what_the_callback_will_need()
    {
        var smart = Smart(_ => Json(Configuration));

        var prepared = Assert.IsType<LaunchOutcome.Prepared>(
            await smart.BeginAsync(Iss, "launch-123", RedirectUri, TestContext.Current.CancellationToken));

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

        var first = Assert.IsType<LaunchOutcome.Prepared>(await smart.BeginAsync(Iss, "l", RedirectUri, TestContext.Current.CancellationToken));
        var second = Assert.IsType<LaunchOutcome.Prepared>(await smart.BeginAsync(Iss, "l", RedirectUri, TestContext.Current.CancellationToken));

        Assert.NotEqual(first.State, second.State);
        Assert.NotEqual(first.Session.CodeVerifier, second.Session.CodeVerifier);
    }

    // ---- Completing a launch ----------------------------------------------

    [Fact]
    public async Task An_error_from_the_ehr_is_reported_with_its_description()
    {
        var smart = Smart(_ => throw new Xunit.Sdk.XunitException("No token call should be made."));

        var outcome = Assert.IsType<CallbackOutcome.AuthorizationDenied>(
            await smart.CompleteAsync(null, null, "access_denied", "The user said no", Session, TestContext.Current.CancellationToken));

        Assert.Equal("The user said no", outcome.Reason);
    }

    [Fact]
    public async Task An_error_without_a_description_falls_back_to_the_code()
    {
        var smart = Smart(_ => throw new Xunit.Sdk.XunitException("No token call should be made."));

        var outcome = Assert.IsType<CallbackOutcome.AuthorizationDenied>(
            await smart.CompleteAsync(null, null, "access_denied", null, Session, TestContext.Current.CancellationToken));

        Assert.Equal("access_denied", outcome.Reason);
    }

    [Fact]
    public async Task A_callback_with_no_launch_in_flight_is_refused_before_the_token_call()
    {
        var smart = Smart(_ => throw new Xunit.Sdk.XunitException("No token call should be made."));

        Assert.IsType<CallbackOutcome.UnknownLaunch>(
            await smart.CompleteAsync("the-code", "the-state", null, null, launch: null, TestContext.Current.CancellationToken));
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

        await smart.CompleteAsync("the-code", "the-state", null, null, Session, TestContext.Current.CancellationToken);

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
            await smart.CompleteAsync("the-code", "the-state", null, null, Session, TestContext.Current.CancellationToken));

        Assert.Equal(400, outcome.Status);
        Assert.Contains("invalid_grant", outcome.Reason);
    }

    [Fact]
    public async Task A_token_response_without_an_access_token_is_not_trusted()
    {
        var smart = Smart(_ => Json("""{"patient":"pat-1"}"""));

        Assert.IsType<CallbackOutcome.NoAccessToken>(
            await smart.CompleteAsync("the-code", "the-state", null, null, Session, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_token_response_without_patient_context_stops_before_reading_anything()
    {
        var smart = Smart(_ => Json("""{"access_token":"tok"}"""));

        Assert.IsType<CallbackOutcome.NoPatientContext>(
            await smart.CompleteAsync("the-code", "the-state", null, null, Session, TestContext.Current.CancellationToken));
    }

    // ---- Plumbing ---------------------------------------------------------

    private const string Configuration = """
        {"authorization_endpoint":"https://ehr.example/authorize","token_endpoint":"https://ehr.example/token"}
        """;

    private static SmartLaunch Smart(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubClientFactory(new StubHandler(respond)),
            Options.Create(new SmartOptions { TrustedIssuers = [Iss] }),
            NullLogger<SmartLaunch>.Instance);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
