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
        var html = await app.GetAsync("/launch?iss=http://127.0.0.1:1/fhir&launch=irrelevant");

        Assert.Contains("Could not read the SMART configuration", html);
    }

    [Fact]
    public async Task The_home_page_shows_the_launch_url_to_paste_into_the_ehr()
    {
        var html = await app.GetAsync("/");

        Assert.Contains($"http://{AppFixture.AppHost}/launch", html);
    }
}
