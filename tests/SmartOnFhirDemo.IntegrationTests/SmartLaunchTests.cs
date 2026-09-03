using System.Net;
using System.Text.RegularExpressions;

namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// Drives the app through a real SMART EHR launch against the real launcher. The
/// assertions are deliberately about shape rather than values: the FHIR server behind
/// the launcher is a public sandbox whose data can change without warning.
/// </summary>
public class SmartLaunchTests(LauncherFixture launcher) : IClassFixture<LauncherFixture>
{
    /// <summary>Gates every test here: without a launcher there is nothing to launch against.</summary>
    public static bool LauncherIsRunning => Launcher.IsRunning;

    private const string NeedsLauncher =
        "Needs a running SMART App Launcher; set "
        + Launcher.UrlVariable
        + " (see docs/development.md).";

    /// <summary>
    /// The rows beneath the banner. The name and what identifies the patient are in the
    /// banner itself, which is what <see cref="Banner"/> reads.
    /// </summary>
    private static readonly string[] DetailLabels = ["Address", "Phone", "Marital status"];

    /// <summary>Runs a whole launch — discovery, authorize, token, patient read — as one GET.</summary>
    private async Task<string> LaunchAsync(string authError = "")
    {
        using var client = launcher.CreateChainClient();

        var (_, html) = await LaunchAsync(client, launcher.PatientId, authError);
        return html;
    }

    /// <summary>
    /// The same launch down a client the caller owns, so two launches can share a cookie
    /// jar and be two tabs of one browser. Returns where the launch landed as well as what
    /// it rendered: that URL is the launch's name, and going back to it is the point.
    /// </summary>
    private async Task<(Uri Landed, string Html)> LaunchAsync(
        HttpClient client,
        string patientId,
        string authError = ""
    )
    {
        var launch = LaunchParams.Encode(patientId, launcher.ProviderId, authError);
        var url =
            $"/launch?iss={Uri.EscapeDataString(Launcher.Iss(launch))}"
            + $"&launch={Uri.EscapeDataString(launch)}";

        using var response = await client.GetAsync(url);

        Assert.True(
            response.IsSuccessStatusCode,
            $"The launch ended at {(int)response.StatusCode}."
        );

        return (response.RequestMessage!.RequestUri!, await response.Content.ReadAsStringAsync());
    }

    // ---- The happy path ---------------------------------------------------

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task An_ehr_launch_renders_the_patient_summary()
    {
        var html = await LaunchAsync();

        Assert.Contains("Patient summary", html);
        Assert.NotEmpty(Banner(html));
        foreach (var label in DetailLabels)
            Assert.Contains($"<dt>{label}</dt>", html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_summary_is_populated_from_the_patient_that_was_read()
    {
        var html = await LaunchAsync();

        // Which patient the sandbox serves is not the point; that the app actually
        // read one and projected it is.
        Assert.NotEqual("Unnamed patient", Banner(html));
        Assert.NotEmpty(Meta(html));
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task An_ehr_launch_names_the_practitioner_who_started_it()
    {
        var html = await LaunchAsync();

        // Which practitioner the sandbox serves is not the point; that the id_token was
        // validated, its fhirUser followed, and a real name read back is.
        Assert.Contains("Launched by", html);
        Assert.DoesNotContain("no id_token", html);
        Assert.DoesNotContain("could not be validated", html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_raw_patient_resource_is_rendered_html_encoded()
    {
        var html = await LaunchAsync();

        Assert.Contains("Raw Patient resource", html);

        // Encoded, not raw: the resource comes from the EHR, so it must not be able
        // to inject markup into the page that displays it.
        Assert.Contains("&quot;resourceType&quot;: &quot;Patient&quot;", html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task Starting_a_launch_hands_out_no_session_before_there_is_one_to_hold()
    {
        // A session is what holds a launch. On the way out to the EHR there is no launch
        // yet — only one in flight, which the cache keys by state — so there is nothing
        // for a cookie to name and none is set.
        var launch = LaunchParams.Encode(launcher.PatientId, launcher.ProviderId);

        using var client = launcher.CreateDirectClient();
        using var response = await client.GetAsync(
            $"/launch?iss={Uri.EscapeDataString(Launcher.Iss(launch))}"
                + $"&launch={Uri.EscapeDataString(launch)}",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith(BrowserSession.CookieName, StringComparison.Ordinal)
        );
    }

    // ---- Two open patients ------------------------------------------------
    //
    // The end-to-end proof, and it only runs where a launcher does — which is the nightly
    // job, not a pull request. The tests standing guard on every push are the ones in
    // LaunchSessionTests; these are what say the guarantee survives two real launches.

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task A_second_launch_does_not_take_the_first_ones_patient_away()
    {
        // One client, one cookie jar: two tabs of one browser. If the session held a
        // single launch, the second would overwrite the first and the first tab would
        // then render the second patient under the first patient's banner.
        using var client = launcher.CreateChainClient();

        var (first, firstHtml) = await LaunchAsync(client, launcher.PatientId);
        var (second, _) = await LaunchAsync(client, launcher.OtherPatientId);

        Assert.NotEqual(first, second);

        using var again = await client.GetAsync(first, TestContext.Current.CancellationToken);
        Assert.True(again.IsSuccessStatusCode, $"Going back ended at {(int)again.StatusCode}.");

        var againHtml = await again.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );

        Assert.Equal(Banner(firstHtml), Banner(againHtml));
        Assert.Equal(Meta(firstHtml), Meta(againHtml));
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task A_summary_claiming_the_wrong_patient_renders_nothing()
    {
        using var client = launcher.CreateChainClient();

        var (landed, _) = await LaunchAsync(client, launcher.PatientId);

        // The same launch, told it is showing somebody else. Every value but one is the
        // one the app itself issued, which is what makes this a check rather than a guess.
        var claimed = new Uri(
            landed.GetLeftPart(UriPartial.Path)
                + $"?id={Uri.EscapeDataString(LaunchId(landed))}"
                + $"&patient={Uri.EscapeDataString(launcher.OtherPatientId)}"
        );

        using var response = await client.GetAsync(claimed, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("This launch is no longer open", html);
        Assert.DoesNotContain("<dt>MRN</dt>", html);
    }

    private static string LaunchId(Uri summary) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(summary.Query)["id"]!;

    // ---- Reading on from the summary --------------------------------------

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_summary_offers_the_panels_the_launch_asked_for_scopes_for()
    {
        var html = await LaunchAsync();

        foreach (var panel in ChartPanel.All)
            Assert.Contains($"show={panel.Slug}", html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task A_panel_reads_on_from_the_launch_that_is_already_open()
    {
        using var client = launcher.CreateChainClient();

        var (landed, _) = await LaunchAsync(client, launcher.PatientId);

        using var response = await client.GetAsync(
            new Uri($"{landed}&show={ChartPanel.Conditions.Slug}"),
            TestContext.Current.CancellationToken
        );
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Which conditions the sandbox serves is not the point, and it can be reseeded;
        // that the launch was still live enough to search on is.
        Assert.Contains(ChartPanel.Conditions.Title, html);
        Assert.DoesNotContain("no longer open", html);
        Assert.DoesNotContain("would not return", html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task An_ehr_that_will_not_grant_the_scopes_refuses_the_whole_launch()
    {
        // Worth pinning because it is not what you would guess. Asked to grant less than
        // the app requested, this EHR does not hand back a narrowed token — it refuses at
        // the authorization step and names every scope it would not give. So a launch
        // here cannot be used to show a granted-versus-requested gap on the summary: there
        // is no launch to show it on. See ideas.md, under SMART v2 granular scopes.
        var launch = LaunchParams.Encode(
            launcher.PatientId,
            launcher.ProviderId,
            grantedScope: "launch openid fhirUser patient/Patient.read"
        );

        using var client = launcher.CreateChainClient();
        using var response = await client.GetAsync(
            $"/launch?iss={Uri.EscapeDataString(Launcher.Iss(launch))}"
                + $"&launch={Uri.EscapeDataString(launch)}",
            TestContext.Current.CancellationToken
        );
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("The EHR refused the authorization request", html);
        Assert.Contains("patient/Condition.read", html);
    }

    // ---- Failures the launcher can simulate for real ----------------------

    [Theory(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    [InlineData("auth_invalid_client_id")]
    [InlineData("auth_invalid_scope")]
    [InlineData("auth_invalid_redirect_uri")]
    public async Task An_authorization_error_from_the_ehr_reaches_the_error_page(string authError)
    {
        var html = await LaunchAsync(authError);

        Assert.Contains("The EHR refused the authorization request", html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task A_rejected_token_exchange_reaches_the_error_page()
    {
        var html = await LaunchAsync("token_invalid_token");

        Assert.Contains("Token exchange failed", html);
    }

    // ---- The same launch, narrated ----------------------------------------

    /// <summary>
    /// Drives the narrated launch as far as the pause before the exchange: the opening
    /// page, then the trip out to the EHR and back to /learn/callback. The exchange
    /// itself is a form post, and is covered against a stub in the unit tests.
    /// </summary>
    private async Task<(string Opening, string Pause)> LearnAsync()
    {
        using var client = launcher.CreateChainClient();
        return await LearnAsync(client);
    }

    /// <summary>
    /// The same walk down a client the caller owns, for tests that carry on past the
    /// pause: the exchange is antiforgery-protected, so it needs the jar that holds the
    /// cookie the pause was served with.
    /// </summary>
    private async Task<(string Opening, string Pause)> LearnAsync(HttpClient client)
    {
        var launch = LaunchParams.Encode(launcher.PatientId, launcher.ProviderId);
        var url =
            $"/learn?iss={Uri.EscapeDataString(Launcher.Iss(launch))}"
            + $"&launch={Uri.EscapeDataString(launch)}";

        using var opening = await client.GetAsync(url);
        Assert.True(
            opening.IsSuccessStatusCode,
            $"The narrated launch ended at {(int)opening.StatusCode}."
        );
        var openingHtml = await opening.Content.ReadAsStringAsync();

        // The continue button leads where the plain launch would have sent a 302.
        var authorize = WebUtility.HtmlDecode(
            Regex
                .Match(openingHtml, "class=\"button go\" href=\"(?<url>[^\"]+)\"")
                .Groups["url"]
                .Value
        );
        Assert.NotEmpty(authorize);

        using var pause = await client.GetAsync(authorize);
        Assert.True(
            pause.IsSuccessStatusCode,
            $"The EHR round trip ended at {(int)pause.StatusCode}."
        );

        return (openingHtml, await pause.Content.ReadAsStringAsync());
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_narrated_launch_explains_the_request_before_it_is_sent()
    {
        var (opening, _) = await LearnAsync();

        Assert.Contains("What the EHR sent", opening);
        Assert.Contains("What the app discovered", opening);
        Assert.Contains("What the app is about to send", opening);

        // The endpoints on the page came out of the EHR's own configuration, not a guess.
        Assert.Contains("/auth/authorize", opening);
        Assert.Contains("/auth/token", opening);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_narrated_callback_pauses_on_a_code_it_has_not_spent()
    {
        var (_, pause) = await LearnAsync();

        Assert.Contains("The EHR sent your browser back", pause);

        // Shown as an abbreviation with its length, never in full.
        Assert.Matches(@"<dt>code</dt>\s*<dd>\s*<code>[^<]*characters\)</code>", pause);

        // The verifier is named on the pending request, and withheld from it.
        Assert.Contains("code_verifier", pause);
        Assert.Contains("(withheld)", pause);
    }

    /// <summary>
    /// Posts the pause's form back. That spends the code, opens the session, and lands on
    /// a URL naming the launch — which is what every page after it navigates by.
    /// </summary>
    private static async Task<(Uri Landed, string Html)> ExchangeAsync(
        HttpClient client,
        string pauseHtml
    )
    {
        using var exchange = new HttpRequestMessage(HttpMethod.Post, "/learn/callback")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Code"] = Hidden(pauseHtml, "Code"),
                    ["State"] = Hidden(pauseHtml, "State"),
                    ["__RequestVerificationToken"] = Hidden(
                        pauseHtml,
                        "__RequestVerificationToken"
                    ),
                }
            ),
        };

        using var exchanged = await client.SendAsync(
            exchange,
            TestContext.Current.CancellationToken
        );
        Assert.True(
            exchanged.IsSuccessStatusCode,
            $"The exchange ended at {(int)exchanged.StatusCode}."
        );

        var landed = exchanged.RequestMessage!.RequestUri!;

        // The state that got us here is spent; the walkthrough carries on by launch id.
        Assert.Contains("id=", landed.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("state=", landed.Query, StringComparison.Ordinal);

        return (
            landed,
            await exchanged.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_narrated_launch_explains_who_started_it()
    {
        using var client = launcher.CreateChainClient();

        var (_, pause) = await LearnAsync(client);
        var (landed, _) = await ExchangeAsync(client, pause);

        using var identity = await client.GetAsync(
            new Uri($"/learn/user{landed.Query}", UriKind.Relative),
            TestContext.Current.CancellationToken
        );
        var html = await identity.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Who launched this app", html);

        // The claim was followed to a real resource, not merely reported.
        Assert.DoesNotContain("Nobody, as far as this launch can prove", html);
        Assert.Contains("Practitioner", html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_narrated_launch_says_what_the_session_it_opened_holds()
    {
        using var client = launcher.CreateChainClient();

        var (_, pause) = await LearnAsync(client);

        // The exchange lands straight on the token page, which carries steps 5 and 6.
        var (_, html) = await ExchangeAsync(client, pause);

        Assert.Contains("The session this launch opened", html);

        // The two halves that name a launch are described; neither is shown.
        Assert.Contains(BrowserSession.CookieName, html);
        Assert.Contains("SameSite", html);
        Assert.Contains(Smart.Withheld, html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_narrated_launch_ends_with_the_same_panels_the_plain_one_offers()
    {
        // Feature parity is the claim /learn makes by narrating this app rather than a
        // simpler one, and the last page is where it would quietly stop being true.
        using var client = launcher.CreateChainClient();

        var (_, pause) = await LearnAsync(client);
        var (landed, _) = await ExchangeAsync(client, pause);

        using var response = await client.GetAsync(
            new Uri(
                $"/learn/patient{landed.Query}&show={ChartPanel.Conditions.Slug}",
                UriKind.Relative
            ),
            TestContext.Current.CancellationToken
        );
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        foreach (var panel in ChartPanel.All)
            Assert.Contains($"show={panel.Slug}", html);

        Assert.Contains(ChartPanel.Conditions.Title, html);
        Assert.DoesNotContain("no longer open", html);

        // Still the walkthrough, not a second summary page: the narration is above it.
        Assert.Contains("The launch is done", html);
    }

    // ---- Helpers ----------------------------------------------------------

    /// <summary>Reads a hidden form input the narrated pause carries forward.</summary>
    private static string Hidden(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{name}\"[^>]*value=\"(?<value>[^\"]+)\"");

        Assert.True(match.Success, $"The pause carried no '{name}' to post back.");
        return WebUtility.HtmlDecode(match.Groups["value"].Value);
    }

    /// <summary>The name the app's banner shows, which is where the patient is named now.</summary>
    private static string Banner(string html) => Group(html, "<h2>(?<value>.*?)</h2>", "banner");

    /// <summary>The line beneath it: gender, birth date and MRN, as the banner renders them.</summary>
    private static string Meta(string html) =>
        Group(html, "<p class=\"meta\">(?<value>.*?)</p>", "banner meta");

    private static string Group(string html, string pattern, string what)
    {
        var match = Regex.Match(html, pattern, RegexOptions.Singleline);

        Assert.True(match.Success, $"The app rendered no {what}.");
        return match.Groups["value"].Value.Trim();
    }
}
