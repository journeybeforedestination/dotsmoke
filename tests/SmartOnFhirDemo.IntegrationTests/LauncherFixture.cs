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
    private string? _otherPatientId;
    private string? _providerId;

    /// <summary>A patient the launcher's FHIR server actually holds, read at start-up.</summary>
    public string PatientId => Discovered(_patientId);

    /// <summary>
    /// A second, different patient. Two open patients in one browser is the case launch
    /// isolation exists for, and it cannot be tested with one.
    /// </summary>
    public string OtherPatientId => Discovered(_otherPatientId);

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

        var patients = await IdsAsync(http, "Patient", count: 2);
        (_patientId, _otherPatientId) = (patients[0], patients[1]);

        _providerId = (await IdsAsync(http, "Practitioner", count: 1))[0];
    }

    /// <summary>
    /// Reads ids rather than hard-coding them: the public sandbox behind the launcher can
    /// be reseeded, and no assertion here depends on which records they are.
    /// </summary>
    private static async Task<IReadOnlyList<string?>> IdsAsync(
        HttpClient http,
        string resourceType,
        int count
    )
    {
        var bundle = await http.GetStringAsync(
            $"{Launcher.Url}/v/r4/fhir/{resourceType}?_count={count}"
        );

        return
        [
            .. JsonDocument
                .Parse(bundle)
                .RootElement.GetProperty("entry")
                .EnumerateArray()
                .Select(entry => entry.GetProperty("resource").GetProperty("id").GetString()),
        ];
    }
}
