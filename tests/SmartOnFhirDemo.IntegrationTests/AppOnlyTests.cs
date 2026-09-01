using System.Net;

namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// The failure paths the app reaches on its own, before or without an EHR. These need
/// no launcher, so they run without Docker.
/// </summary>
public class AppOnlyTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Fact]
    public async Task A_launch_url_opened_directly_explains_itself()
    {
        var html = await app.GetAsync("/launch");

        Assert.Contains("meant to be opened by an EHR", html);
    }

    [Fact]
    public async Task A_launch_missing_only_the_launch_id_is_still_refused()
    {
        var html = await app.GetAsync("/launch?iss=https://ehr.example/fhir");

        Assert.Contains("meant to be opened by an EHR", html);
    }

    [Fact]
    public async Task A_callback_for_an_unknown_launch_is_refused()
    {
        var html = await app.GetAsync("/callback?code=whatever&state=never-issued");

        Assert.Contains("expired or was already completed", html);
    }

    [Fact]
    public async Task A_callback_carrying_an_authorization_error_reports_it()
    {
        var html = await app.GetAsync("/callback?error=access_denied&error_description=Nope");

        Assert.Contains("The EHR refused the authorization request", html);
        Assert.Contains("Nope", html);
    }

    [Fact]
    public async Task A_launch_against_an_unreachable_issuer_is_reported()
    {
        var html = await app.GetAsync(
            $"/launch?iss={AppFixture.UnreachableIssuer}/fhir&launch=irrelevant"
        );

        Assert.Contains("Could not read the SMART configuration", html);
    }

    [Theory]
    [InlineData("https://evil.example/fhir")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://app.test@evil.example/fhir")]
    public async Task A_launch_from_an_untrusted_issuer_is_refused(string iss)
    {
        var html = await app.GetAsync($"/launch?iss={Uri.EscapeDataString(iss)}&launch=irrelevant");

        Assert.Contains("not registered to launch from that EHR", html);
    }

    [Fact]
    public async Task Refusing_an_untrusted_issuer_does_not_echo_it_back()
    {
        var html = await app.GetAsync("/launch?iss=https://evil.example/fhir&launch=irrelevant");

        // The rejected value is attacker-controlled; it belongs in the log, not the page.
        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public async Task The_home_page_shows_the_launch_url_to_paste_into_the_ehr()
    {
        var html = await app.GetAsync("/");

        Assert.Contains($"http://{AppFixture.AppHost}/launch", html);
    }

    [Fact]
    public async Task The_home_page_offers_the_narrated_launch_as_well()
    {
        var html = await app.GetAsync("/");

        Assert.Contains($"http://{AppFixture.AppHost}/learn", html);
    }

    // ---- Two values name a launch, and neither answers alone ---------------

    [Fact]
    public async Task A_summary_without_a_session_cookie_resolves_nothing()
    {
        // A launch id in a URL is not a bearer token: browser history and Referer headers
        // are full of URLs, and none of them carry this app's cookie.
        var html = await app.GetAsync("/summary?id=never-issued");

        Assert.Contains("expired or was already completed", html);
    }

    [Fact]
    public async Task A_summary_with_a_cookie_but_an_unknown_launch_id_resolves_nothing()
    {
        var html = await app.GetAsync(
            "/summary?id=never-issued",
            cookie: $"{BrowserSession.CookieName}=a-browser-that-launched-something-else"
        );

        Assert.Contains("expired or was already completed", html);
    }

    [Fact]
    public async Task A_cookie_on_its_own_does_not_say_which_launch_is_meant()
    {
        var html = await app.GetAsync(
            "/summary",
            cookie: $"{BrowserSession.CookieName}=a-browser-that-launched-something-else"
        );

        Assert.Contains("expired or was already completed", html);
    }

    [Fact]
    public async Task The_summary_refuses_to_be_stored()
    {
        // Unlike the callback it replaced, this URL is stable and can be returned to, so
        // what a browser or a proxy keeps of a page of patient data is worth saying.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync(
            "/summary?id=never-issued",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_proxy_asking_whether_the_app_is_up_is_told_so()
    {
        // The proxy will not send a reader here until this answers, so a rename is an
        // outage that looks like a deploy hanging.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync("/up", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/learn")]
    [InlineData("/error?message=whatever")]
    public async Task Every_response_carries_the_security_headers(string url)
    {
        // Including /error, which is where every launch failure lands.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        // Every header the policy names, verbatim, rather than a restatement of it here:
        // what it should say is SecurityHeadersTests' question, and this is whether the
        // response carries it. The fixture's origin is http, as localhost is.
        foreach (var (name, value) in SecurityHeaders.For(secure: false))
            Assert.Equal(value, response.Headers.GetValues(name).Single());
    }

    [Fact]
    public async Task An_app_not_served_over_tls_does_not_claim_it_is()
    {
        // The fixture's origin is http, as localhost is. HSTS follows the configured
        // origin, so this is the same decision the session cookie's Secure flag makes.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    // ---- The narrated launch fails the same way the plain one does ---------

    [Theory]
    [InlineData("/learn")]
    [InlineData("/learn?iss=https://ehr.example/fhir")]
    public async Task A_narrated_launch_url_opened_directly_explains_itself(string url)
    {
        var html = await app.GetAsync(url);

        Assert.Contains("meant to be opened by an EHR", html);
    }

    [Fact]
    public async Task A_narrated_launch_from_an_untrusted_issuer_is_refused_without_echoing_it()
    {
        var html = await app.GetAsync("/learn?iss=https://evil.example/fhir&launch=irrelevant");

        Assert.Contains("not registered to launch from that EHR", html);

        // Showing the issuer is the whole point of the narrated launch — except this one,
        // which is attacker-controlled and belongs in the log.
        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public async Task A_narrated_identity_step_for_an_unknown_launch_is_refused()
    {
        var html = await app.GetAsync("/learn/user?id=never-issued&patient=pat-1");

        Assert.Contains("no longer open", html);
    }

    [Fact]
    public async Task A_narrated_callback_for_an_unknown_launch_is_refused()
    {
        var html = await app.GetAsync("/learn/callback?code=whatever&state=never-issued");

        Assert.Contains("expired or was already completed", html);
    }

    [Theory]
    [InlineData("/learn/token?id=never-issued&patient=pat-1")]
    [InlineData("/learn/patient?id=never-issued&patient=pat-1")]
    public async Task A_narrated_page_whose_launch_is_gone_asks_for_a_new_one(string url)
    {
        // The narrated launch resolves its own launch exactly as the plain summary does,
        // so it refuses the same way and names the patient the page had been showing.
        var html = await app.GetAsync(url);

        Assert.Contains("This launch is no longer open", html);
        Assert.Contains("pat-1", html);
    }

    [Fact]
    public async Task A_narrated_page_is_no_more_reachable_without_a_cookie_than_the_summary_is()
    {
        // Same two values, same rule: the URL selects and the cookie authenticates.
        var html = await app.GetAsync("/learn/patient?id=never-issued&patient=pat-1");

        Assert.DoesNotContain("<dt>MRN</dt>", html);
    }

    [Theory]
    [InlineData("/learn")]
    [InlineData("/learn/callback")]
    [InlineData("/learn/token?id=never-issued&patient=pat-1")]
    [InlineData("/learn/patient?id=never-issued&patient=pat-1")]
    public async Task Every_narrated_launch_route_refuses_to_be_stored(string url)
    {
        // Not followed: the header goes on whatever the learn route itself answers, and
        // asserting it on all four proves the filter is wired to all four.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        // These pages carry a live authorization code, patient demographics and a whole
        // FHIR resource. Nothing between here and the reader may keep a copy.
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }
}
