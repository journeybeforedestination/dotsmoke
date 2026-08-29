using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using SmartOnFhirDemo;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddScoped<SmartLaunch>();
builder.Services.Configure<SmartOptions>(builder.Configuration.GetSection("Smart"));

var app = builder.Build();
app.UseExceptionHandler("/error");
app.MapRazorPages();

// Step 1 of the SMART EHR launch. The EHR opens this URL in the user's browser with
// the FHIR base URL (iss) and an opaque launch id. SmartLaunch does the protocol; this
// endpoint holds the launch until the EHR redirects back, and turns the outcome into
// a response.
app.MapGet("/launch", async (
    string? iss,
    string? launch,
    HttpRequest request,
    SmartLaunch smart,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    var redirectUri = $"{request.Scheme}://{request.Host}/callback";

    return await smart.BeginAsync(iss, launch, redirectUri, ct) switch
    {
        LaunchOutcome.Prepared prepared => Remember(prepared),

        LaunchOutcome.MissingParameters =>
            Fail("This URL is meant to be opened by an EHR: both 'iss' and 'launch' query parameters are required."),

        LaunchOutcome.DiscoveryFailed(var wellKnown, var reason) =>
            Fail($"Could not read the SMART configuration from {wellKnown} — {reason}"),

        var outcome => throw new UnreachableException($"Unhandled launch outcome: {outcome.GetType().Name}."),
    };

    // Only the state and PKCE verifier need to survive the trip through the EHR.
    IResult Remember(LaunchOutcome.Prepared prepared)
    {
        cache.Set(Smart.CacheKey(prepared.State), prepared.Session, TimeSpan.FromMinutes(5));
        return Results.Redirect(prepared.AuthorizeUrl);
    }

    static IResult Fail(string message) => Results.Redirect($"/error?message={Uri.EscapeDataString(message)}");
});

app.Run();

// Named so the integration tests can host this app with WebApplicationFactory;
// top-level statements otherwise compile to an internal Program class.
public partial class Program;
