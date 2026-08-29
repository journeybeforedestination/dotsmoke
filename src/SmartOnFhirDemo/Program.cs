using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SmartOnFhirDemo;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.Configure<SmartOptions>(builder.Configuration.GetSection("Smart"));

var app = builder.Build();
app.UseExceptionHandler("/error");
app.MapRazorPages();

// Step 1 of the SMART EHR launch. The EHR opens this URL in the user's browser with
// the FHIR base URL (iss) and an opaque launch id. Discover the server's OAuth
// endpoints, remember the PKCE verifier, and bounce the browser on to authorization.
app.MapGet("/launch", async (
    string? iss,
    string? launch,
    HttpRequest request,
    IHttpClientFactory clients,
    IMemoryCache cache,
    IOptions<SmartOptions> options,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(launch))
        return Fail("This URL is meant to be opened by an EHR: both 'iss' and 'launch' query parameters are required.");

    var wellKnown = $"{iss.TrimEnd('/')}/.well-known/smart-configuration";
    SmartConfiguration? config;
    try
    {
        config = await clients.CreateClient().GetFromJsonAsync<SmartConfiguration>(wellKnown, ct);
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
    {
        log.LogWarning(ex, "SMART discovery failed for {WellKnown}", wellKnown);
        return Fail($"Could not read the SMART configuration from {wellKnown} — {ex.Message}");
    }

    if (config is null)
        return Fail($"{wellKnown} returned an empty SMART configuration.");

    var (verifier, challenge) = Smart.NewPkce();
    var state = Smart.NewState();
    var redirectUri = $"{request.Scheme}://{request.Host}/callback";

    cache.Set(
        Smart.CacheKey(state),
        new LaunchState(iss, config.TokenEndpoint, verifier, redirectUri),
        TimeSpan.FromMinutes(5));

    return Results.Redirect(
        Smart.BuildAuthorizeUrl(config, options.Value, redirectUri, iss, launch, state, challenge));

    static IResult Fail(string message) => Results.Redirect($"/error?message={Uri.EscapeDataString(message)}");
});

app.Run();
