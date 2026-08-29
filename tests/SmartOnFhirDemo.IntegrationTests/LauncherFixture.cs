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

    /// <summary>A patient the launcher's FHIR server actually holds, read at start-up.</summary>
    public string PatientId => _patientId
        ?? throw new InvalidOperationException($"No launcher at ${Launcher.UrlVariable}.");

    /// <summary>The FHIR base URL for a launch — the launcher encodes its settings into the path.</summary>
    public string Iss(string launch) => $"{Launcher.Url}/v/r4/sim/{launch}/fhir";

    public async ValueTask InitializeAsync()
    {
        if (Launcher.Url is null) return;

        using var http = new HttpClient();
        var bundle = await http.GetStringAsync($"{Launcher.Url}/v/r4/fhir/Patient?_count=1");

        // Read a patient id rather than hard-coding one: the public sandbox behind the
        // launcher can be reseeded, and no assertion here depends on which patient it is.
        _patientId = JsonDocument.Parse(bundle)
            .RootElement.GetProperty("entry")[0]
            .GetProperty("resource").GetProperty("id").GetString();
    }
}
