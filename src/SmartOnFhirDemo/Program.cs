using Microsoft.Extensions.Caching.Memory;
using SmartOnFhirDemo;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddScoped<SmartLaunch>();
builder.Services.AddScoped<Jwks>();

// The seam the id_token's lifetime is checked against, so a test can hold the clock still.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<SmartOptions>(builder.Configuration.GetSection("Smart"));

var app = builder.Build();
app.UseExceptionHandler("/error");
app.MapRazorPages();

// Step 1 of the SMART EHR launch. The EHR opens this URL in the user's browser with
// the FHIR base URL (iss) and an opaque launch id. SmartLaunch does the protocol; this
// endpoint holds the launch until the EHR redirects back, and turns the outcome into
// a response.
app.MapGet(
    "/launch",
    async (
        string? iss,
        string? launch,
        HttpRequest request,
        SmartLaunch smart,
        IMemoryCache cache,
        CancellationToken ct
    ) =>
    {
        var redirectUri = $"{request.Scheme}://{request.Host}/callback";

        var outcome = await smart.BeginAsync(iss, launch, redirectUri, ct);

        return outcome is LaunchOutcome.Prepared prepared
            ? Remember(prepared)
            : Fail(LaunchMessages.For(outcome));

        IResult Remember(LaunchOutcome.Prepared prepared)
        {
            cache.Remember(prepared);
            return Results.Redirect(prepared.AuthorizeUrl);
        }

        static IResult Fail(string message) =>
            Results.Redirect($"/error?message={Uri.EscapeDataString(message)}");
    }
);

app.Run();

// Named so the integration tests can host this app with WebApplicationFactory;
// top-level statements otherwise compile to an internal Program class.
public partial class Program;
