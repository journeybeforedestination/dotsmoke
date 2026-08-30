using System.Text.Json;

namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// Points the tests at a running SMART App Launcher. Omarchy deliberately keeps
/// users out of the root-equivalent <c>docker</c> group, so the container is not
/// started from here — it is expected to be up already, and the tests that need
/// it skip when it is not. See the README for the one command that starts it.
/// </summary>
public sealed class LauncherFixture : AppFixture, IAsyncLifetime
{
    private string? _patientId;
    private string? _providerId;

    /// <summary>A patient the launcher's FHIR server actually holds, read at start-up.</summary>
    public string PatientId => Discovered(_patientId);

    /// <summary>
    /// A practitioner to launch as. Needed because the app asks for <c>openid</c>: the
    /// launcher has to name a user in the id_token, and a simulation that selects no
    /// provider cannot honour skip_login — it stops at /provider-login instead of
    /// redirecting back, and every launch here would end on a login page.
    /// </summary>
    public string ProviderId => Discovered(_providerId);

    private static string Discovered(string? id) =>
        id ?? throw new InvalidOperationException($"No launcher at ${Launcher.UrlVariable}.");

    public async ValueTask InitializeAsync()
    {
        if (Launcher.Url is null)
            return;

        using var http = new HttpClient();

        _patientId = await FirstIdAsync(http, "Patient");
        _providerId = await FirstIdAsync(http, "Practitioner");
    }

    /// <summary>
    /// Reads an id rather than hard-coding one: the public sandbox behind the launcher can
    /// be reseeded, and no assertion here depends on which record it is.
    /// </summary>
    private static async Task<string?> FirstIdAsync(HttpClient http, string resourceType)
    {
        var bundle = await http.GetStringAsync($"{Launcher.Url}/v/r4/fhir/{resourceType}?_count=1");

        return JsonDocument
            .Parse(bundle)
            .RootElement.GetProperty("entry")[0]
            .GetProperty("resource")
            .GetProperty("id")
            .GetString();
    }
}
