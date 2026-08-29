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
        "Needs a running SMART App Launcher; set " + Launcher.UrlVariable + " (see the README).";

    private static readonly string[] SummaryLabels =
        ["Name", "Gender", "Birth date", "MRN", "Address", "Phone", "Marital status"];

    private const string Absent = "—";

    /// <summary>Runs a whole launch — discovery, authorize, token, patient read — as one GET.</summary>
    private async Task<string> LaunchAsync(string authError = "")
    {
        var launch = LaunchParams.Encode(launcher.PatientId, authError);
        var url = $"/launch?iss={Uri.EscapeDataString(launcher.Iss(launch))}" +
                  $"&launch={Uri.EscapeDataString(launch)}";

        using var client = launcher.CreateChainClient();
        using var response = await client.GetAsync(url);

        Assert.True(response.IsSuccessStatusCode,
            $"The launch ended at {(int)response.StatusCode}.");

        return await response.Content.ReadAsStringAsync();
    }

    // ---- The happy path ---------------------------------------------------

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task An_ehr_launch_renders_the_patient_summary()
    {
        var html = await LaunchAsync();

        Assert.Contains("Patient summary", html);
        foreach (var label in SummaryLabels)
            Assert.Contains($"<dt>{label}</dt>", html);
    }

    [Fact(Skip = NeedsLauncher, SkipUnless = nameof(LauncherIsRunning))]
    public async Task The_summary_is_populated_from_the_patient_that_was_read()
    {
        var html = await LaunchAsync();

        // Which patient the sandbox serves is not the point; that the app actually
        // read one and projected it is.
        Assert.NotEqual(Absent, FieldValue(html, "Name"));
        Assert.NotEqual(Absent, FieldValue(html, "Gender"));
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

    // ---- Helpers ----------------------------------------------------------

    private static string FieldValue(string html, string label)
    {
        var match = Regex.Match(
            html,
            $"<dt>{Regex.Escape(label)}</dt>\\s*<dd>(?<value>.*?)</dd>",
            RegexOptions.Singleline);

        Assert.True(match.Success, $"The summary had no '{label}' row.");
        return match.Groups["value"].Value.Trim();
    }
}
