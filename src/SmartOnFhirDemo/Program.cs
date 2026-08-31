using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartOnFhirDemo;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddScoped<SmartLaunch>();
builder.Services.AddScoped<Jwks>();

// The one thing here that survives a restart. Everything else this app holds is a
// credential or patient data, and stays in memory on purpose.
builder.Services.AddDbContext<AccessLogContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("AccessLog"))
);
builder.Services.AddScoped<AccessLog>();

// The seam the id_token's lifetime is checked against, so a test can hold the clock still.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<SmartOptions>(builder.Configuration.GetSection("Smart"));

var app = builder.Build();

// Right for a single-instance demo, and wrong anywhere it is not one: with replicas,
// migrating is a deploy step run once rather than a race between processes starting.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AccessLogContext>().Database.Migrate();

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
        HttpContext http,
        SmartLaunch smart,
        IMemoryCache cache,
        CancellationToken ct
    ) =>
    {
        var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}/callback";

        var outcome = await smart.BeginAsync(iss, launch, redirectUri, ct);

        return outcome is LaunchOutcome.Prepared prepared
            ? Remember(prepared)
            : Fail(LaunchMessages.For(outcome));

        IResult Remember(LaunchOutcome.Prepared prepared)
        {
            // The browser gets its id where it starts a launch, not where it comes back
            // from one, so a launch that is abandoned at the EHR leaves the same trace as
            // one that is not.
            BrowserSession.Establish(http);

            cache.Remember(prepared);
            return Results.Redirect(prepared.AuthorizeUrl);
        }
    }
);

// Steps 2 and 3, and where this app becomes stateful. SmartLaunch trades the code for an
// access token and reads the patient; this files the result against the browser and hands
// the reader a URL naming it.
//
// It renders nothing, deliberately. The authorization code leaves the address bar with the
// redirect, and a refresh stops re-sending a code that has already been spent.
app.MapGet(
    "/callback",
    async (
        string? code,
        string? state,
        string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        HttpContext http,
        SmartLaunch smart,
        IMemoryCache cache,
        CancellationToken ct
    ) =>
    {
        var (outcome, context) = await smart.CompleteAsync(
            code,
            state,
            error,
            errorDescription,
            cache.ClaimLaunch(state),
            ct
        );

        if (outcome is not CallbackOutcome.Completed completed || context is null)
            return Fail(LaunchMessages.For(outcome));

        cache.RememberLaunch(BrowserSession.Establish(http), context, completed);

        return Results.Redirect($"/summary?id={Uri.EscapeDataString(context.LaunchId)}");
    }
);

app.Run();

// Every way a launch can fail lands on the same page, with a sentence saying which.
static IResult Fail(string message) =>
    Results.Redirect($"/error?message={Uri.EscapeDataString(message)}");

// Named so the integration tests can host this app with WebApplicationFactory;
// top-level statements otherwise compile to an internal Program class.
public partial class Program;
