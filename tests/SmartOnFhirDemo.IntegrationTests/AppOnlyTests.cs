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
        var html = await app.GetAsync($"/launch?iss={AppFixture.UnreachableIssuer}/fhir&launch=irrelevant");

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
    public async Task A_narrated_callback_for_an_unknown_launch_is_refused()
    {
        var html = await app.GetAsync("/learn/callback?code=whatever&state=never-issued");

        Assert.Contains("expired or was already completed", html);
    }

    [Theory]
    [InlineData("/learn/token?state=never-issued")]
    [InlineData("/learn/patient?state=never-issued")]
    public async Task A_walkthrough_page_without_a_transcript_says_so(string url)
    {
        var html = await app.GetAsync(url);

        Assert.Contains("This walkthrough has expired", html);
    }

    [Theory]
    [InlineData("/learn")]
    [InlineData("/learn/callback")]
    [InlineData("/learn/token?state=never-issued")]
    [InlineData("/learn/patient?state=never-issued")]
    public async Task Every_narrated_launch_route_refuses_to_be_stored(string url)
    {
        // Not followed: the header goes on whatever the learn route itself answers, and
        // asserting it on all four proves the filter is wired to all four.
        using var client = app.CreateDirectClient();
        using var response = await client.GetAsync(url);

        // These pages carry a live authorization code, patient demographics and a whole
        // FHIR resource. Nothing between here and the reader may keep a copy.
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }
}
