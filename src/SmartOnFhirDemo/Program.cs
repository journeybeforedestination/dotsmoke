using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SmartOnFhirDemo;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// Named so its handler can be taken from the pool and wrapped per launch by the access
// log. Nothing else is configured on it yet.
builder.Services.AddHttpClient(FhirClients.Name);
builder.Services.AddScoped<SmartLaunch>();
builder.Services.AddScoped<FhirClients>();
builder.Services.AddScoped<Chart>();
builder.Services.AddScoped<Jwks>();

// The one thing here that survives a restart. Everything else this app holds is a
// credential or patient data, and stays in memory on purpose.
builder.Services.AddDbContext<AccessLogContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("AccessLog"))
);
builder.Services.AddScoped<AccessLog>();

// The seam the id_token's lifetime is checked against, so a test can hold the clock still.
builder.Services.AddSingleton(TimeProvider.System);

// Configuration that is wrong should stop the app here rather than surface as a launch
// failure describing app registration, which sends you looking in the wrong place.
builder
    .Services.AddOptions<SmartOptions>()
    .Bind(builder.Configuration.GetSection("Smart"))
    .Validate(
        options => Smart.IsOrigin(options.PublicOrigin),
        "Smart:PublicOrigin is required, and must be an absolute http(s) URL with no path: "
            + "this app is told the address readers reach it on rather than inferring one."
    )
    .Validate(
        options => options.TrustedIssuers.Length > 0,
        "Smart:TrustedIssuers is empty, so no EHR could ever launch this app."
    )
    .ValidateOnStart();

var app = builder.Build();

// ValidateOnStart fires when the host starts, which is after the migration below, so a
// misconfigured app would leave a database behind on its way to refusing to run.
app.Services.GetRequiredService<IStartupValidator>().Validate();

// Right for a single-instance demo, and wrong anywhere it is not one: with replicas,
// migrating is a deploy step run once rather than a race between processes starting.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AccessLogContext>().Database.Migrate();

app.UseExceptionHandler("/error");
app.MapRazorPages();

// What a proxy asks before it sends a reader here. The path is kamal-proxy's default, so
// matching it means a deployment configures no health check at all.
//
// Shallow on purpose: the migration above runs before the app serves, so a volume that is
// missing or unwritable has already stopped the container, and a check that reopened the
// database every second would re-prove that forever.
app.MapGet("/up", () => Results.Ok());

// Step 1 of the SMART EHR launch. The EHR opens this URL in the user's browser with
// the FHIR base URL (iss) and an opaque launch id. SmartLaunch does the protocol; this
// endpoint holds the launch until the EHR redirects back, and turns the outcome into
// a response.
app.MapGet(
    "/launch",
    async (
        string? iss,
        string? launch,
        IOptions<SmartOptions> options,
        SmartLaunch smart,
        IMemoryCache cache,
        CancellationToken ct
    ) =>
    {
        var redirectUri = options.Value.Url("/callback");

        var outcome = await smart.BeginAsync(iss, launch, redirectUri, ct);

        return outcome is LaunchOutcome.Prepared prepared
            ? Remember(prepared)
            : Fail(LaunchMessages.For(outcome));

        IResult Remember(LaunchOutcome.Prepared prepared)
        {
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
        IOptions<SmartOptions> options,
        SmartLaunch smart,
        IMemoryCache cache,
        TimeProvider clock,
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

        cache.RememberLaunch(
            BrowserSession.Establish(http, options.Value.IsSecure),
            context,
            completed,
            clock
        );

        // The patient goes in the URL so every page after this says which one it believes
        // it is showing, and can be told it is wrong.
        return Results.Redirect(
            $"/summary?id={Uri.EscapeDataString(context.LaunchId)}"
                + $"&patient={Uri.EscapeDataString(context.PatientId)}"
        );
    }
);

app.Run();

// Every way a launch can fail lands on the same page, with a sentence saying which.
static IResult Fail(string message) =>
    Results.Redirect($"/error?message={Uri.EscapeDataString(message)}");

// Named so the integration tests can host this app with WebApplicationFactory;
// top-level statements otherwise compile to an internal Program class.
public partial class Program;
