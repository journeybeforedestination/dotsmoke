using Microsoft.AspNetCore.Mvc.Testing;

namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// Hosts the app in memory. Everything the app decides for itself — missing launch
/// parameters, an unknown callback, an unreachable issuer — can be tested against
/// this alone, without paying for the launcher container.
/// </summary>
public class AppFixture : IAsyncDisposable
{
    /// <summary>The host the in-memory app answers on, distinct from the launcher's.</summary>
    public const string AppHost = "app.test";

    private readonly WebApplicationFactory<Program> _app = new();

    /// <summary>
    /// An HTTP client that follows a launch across both the in-memory app and any real
    /// server it redirects to, so a test can express a whole chain as a single GET.
    /// </summary>
    public HttpClient CreateChainClient() =>
        new(new LaunchChainHandler(_app.Server.CreateHandler(), AppHost))
        {
            BaseAddress = new Uri($"http://{AppHost}"),
        };

    public async Task<string> GetAsync(string url)
    {
        using var client = CreateChainClient();
        using var response = await client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

    public virtual async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
