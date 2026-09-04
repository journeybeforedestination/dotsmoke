using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// The failure paths the app reaches on its own, before or without an EHR. These need
/// no launcher, so they run without Docker.
/// </summary>
public class AppOnlyTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Fact]
    public void Every_call_to_an_ehr_goes_through_the_traffic_limiter()
    {
        // The handler is added to ConfigureHttpClientDefaults, but the FHIR client does
        // not take its pipeline from IHttpClientFactory — FhirClients asks
        // IHttpMessageHandlerFactory for the named registration's inner chain and wraps
        // the access log around it. That the defaults reach *that* chain is the fact this
        // asserts, because if they did not the cap would silently not apply to the reads
        // that make up almost all of this app's traffic.
        var factory = app.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        foreach (var name in new[] { "", FhirClients.Name })
            Assert.Contains(
                Chain(factory.CreateHandler(name)),
                handler => handler is EhrTrafficHandler
            );
    }

    /// <summary>Every handler from the outside of a pipeline inwards.</summary>
    private static IEnumerable<HttpMessageHandler> Chain(HttpMessageHandler handler)
    {
        for (var current = handler; current is not null; )
        {
            yield return current;
            current = (current as DelegatingHandler)?.InnerHandler;
        }
    }

    [Fact]
    public async Task A_callback_carrying_an_authorization_error_reports_it()
    {
        var html = await app.GetAsync("/learn/callback?error=access_denied&error_description=Nope");

        Assert.Contains("The EHR refused the authorization request", html);
        Assert.Contains("Nope", html);
    }

    [Fact]
    public async Task A_launch_against_an_unreachable_issuer_is_reported()
    {
        var html = await app.GetAsync(
            $"/learn?iss={AppFixture.UnreachableIssuer}/fhir&launch=irrelevant"
        );

        Assert.Contains("Could not read the SMART configuration", html);
    }

    [Theory]
    [InlineData("https://evil.example/fhir")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://app.test@evil.example/fhir")]
    public async Task A_launch_from_an_untrusted_issuer_is_refused(string iss)
    {
        var html = await app.GetAsync($"/learn?iss={Uri.EscapeDataString(iss)}&launch=irrelevant");

        Assert.Contains("not registered to launch from that EHR", html);
    }

    [Fact]
    public async Task The_home_page_shows_the_launch_url_to_paste_into_the_ehr()
    {
        var html = await app.GetAsync("/");

        Assert.Contains($"http://{AppFixture.AppHost}/learn", html);
    }

    // ---- Two values name a launch, and neither answers alone ---------------

    [Fact]
    public async Task A_chart_without_a_session_cookie_resolves_nothing()
    {
        // A launch id in a URL is not a bearer token: browser history and Referer headers
        // are full of URLs, and none of them carry this app's cookie.
        var html = await app.GetAsync("/learn/patient?id=never-issued");

        Assert.Contains("expired or was already completed", html);
    }

    [Fact]
    public async Task A_chart_with_a_cookie_but_an_unknown_launch_id_resolves_nothing()
    {
        var html = await app.GetAsync(
            "/learn/patient?id=never-issued",
            cookie: $"{BrowserSession.CookieName}=a-browser-that-launched-something-else"
        );

        Assert.Contains("expired or was already completed", html);
    }

    [Fact]
    public async Task A_cookie_on_its_own_does_not_say_which_launch_is_meant()
    {
        var html = await app.GetAsync(
            "/learn/patient",
            cookie: $"{BrowserSession.CookieName}=a-browser-that-launched-something-else"
        );

        Assert.Contains("expired or was already completed", html);
    }

    // ---- The pane a tab swaps in is behind the same guard as the page -----

    [Fact]
    public async Task A_pane_asked_for_without_a_session_is_refused_rather_than_rendered()
    {
        // The pane is a second way into the chart, so it is a second way to read a patient
        // this browser never launched — unless it resolves the launch exactly as the page
        // does. A status rather than a redirect: the script's answer to one is to navigate
        // for real, which lands on the page that explains it.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync(
            "/learn/patient?handler=pane&id=never-issued&patient=123&show=conditions",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_pane_refuses_to_be_stored_for_the_same_reason_the_page_does()
    {
        // It carries the patient data the page carries, and the handler filter that says
        // no-store is on the page rather than on one of its handlers.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync(
            "/learn/patient?handler=pane&id=never-issued",
            TestContext.Current.CancellationToken
        );

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task The_script_the_tabs_need_is_served_and_the_policy_allows_it()
    {
        // It is the app's only static file, and it arrives through MapStaticAssets rather
        // than through middleware this app does not otherwise have. Broken, the tabs fall
        // back to reloading — which works, and would hide this for a long time.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync(
            "/app.js",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "script-src 'self'",
            response.Headers.GetValues("Content-Security-Policy").Single()
        );
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
    [InlineData("/error")]
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
    public async Task The_error_page_says_nothing_a_url_told_it_to()
    {
        // A page that renders whatever its URL carries is a page a stranger can put their
        // own words on, in this app's voice and on this app's domain — phishing that needs
        // no script, so nothing in the CSP would stop it. The sentence comes from TempData,
        // which only this app can write.
        var html = await app.GetAsync("/error?message=Call+1-800-not-us+to+restore+access");

        Assert.DoesNotContain("1-800-not-us", html);
        Assert.Contains("Something went wrong", html);
    }

    [Fact]
    public async Task A_failed_launch_still_reaches_the_error_page_with_its_own_sentence()
    {
        // The other half of the same change: the sentences are still the lesson, they just
        // travel in a cookie the data protection ring signs rather than in the URL.
        var html = await app.GetAsync("/learn?iss=https://evil.example/fhir&launch=irrelevant");

        Assert.Contains("not registered to launch from that EHR", html);
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
        // Every step resolves its launch through the one guard, so they refuse the same
        // way and name the patient the page had been showing.
        var html = await app.GetAsync(url);

        Assert.Contains("This launch is no longer open", html);
        Assert.Contains("pat-1", html);
    }

    [Fact]
    public async Task A_narrated_page_is_no_more_reachable_without_a_cookie_than_any_other()
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
