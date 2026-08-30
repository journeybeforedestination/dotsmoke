using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// Hosts the app in memory. Everything the app decides for itself — missing launch
/// parameters, an unknown callback, an unreachable issuer — can be tested against
/// this alone, without the launcher.
/// </summary>
public class AppFixture : IAsyncDisposable
{
    /// <summary>The host the in-memory app answers on, distinct from the launcher's.</summary>
    public const string AppHost = "app.test";

    /// <summary>
    /// A trusted origin that nothing is listening on, for exercising discovery failure.
    /// Port 1 is reserved and refuses connections immediately.
    /// </summary>
    public const string UnreachableIssuer = "http://127.0.0.1:1";

    private readonly WebApplicationFactory<Program> _app =
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(
                (_, config) => config.AddInMemoryCollection(TrustedIssuers())
            )
        );

    /// <summary>
    /// Replaces the shipped allowlist so the tests launch against their own servers.
    /// Overriding index 0 also proves the app reads the list from configuration.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> TrustedIssuers()
    {
        yield return new("Smart:TrustedIssuers:0", UnreachableIssuer);

        if (Launcher.Url is { } launcher)
            yield return new("Smart:TrustedIssuers:1", launcher);
    }

    /// <summary>
    /// An HTTP client that follows a launch across both the in-memory app and any real
    /// server it redirects to, so a test can express a whole chain as a single GET.
    /// </summary>
    public HttpClient CreateChainClient() =>
        new(new LaunchChainHandler(_app.Server.CreateHandler(), AppHost))
        {
            BaseAddress = new Uri($"http://{AppHost}"),
        };

    /// <summary>
    /// A client that stops at the first response instead of following it, for tests that
    /// assert on a redirect rather than on where it leads.
    /// </summary>
    public HttpClient CreateDirectClient() =>
        new(_app.Server.CreateHandler()) { BaseAddress = new Uri($"http://{AppHost}") };

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
