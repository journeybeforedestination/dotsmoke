using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SmartOnFhirDemo;
using SmartOnFhirDemo.Pages;

var builder = WebApplication.CreateBuilder(args);

// Kestrel names itself in a `Server` header otherwise, which tells a stranger nothing this
// app needs them to know. Untested on purpose: TestServer has no Kestrel, so an assertion
// would pass for a reason unrelated to this line.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddRazorPages();

// Bounded, because /launch is a URL a stranger can open and every one leaves an entry here
// for five minutes. Counted in entries rather than bytes: they are all one of two small
// records.
builder.Services.AddMemoryCache(options => options.SizeLimit = LaunchCache.Entries);

// Discovery and the JWKS go through the unnamed client, and both are documents an EHR
// publishes — small on every server that works. The buffer bound stops one that is not from
// being read into memory whole; the timeout below stops one that never finishes.
builder.Services.AddHttpClient(
    Options.DefaultName,
    client => client.MaxResponseContentBufferSize = 512 * 1024
);

// Named so its handler can be taken from the pool and wrapped per launch by the access log.
// No content bound: a Bundle legitimately is large, and this one answers to a launch that
// was authorized rather than to anyone who can open a URL.
builder.Services.AddHttpClient(FhirClients.Name);

// One limiter for the process, and a singleton because of it: IHttpClientFactory pools
// handler chains and gives each its own scope, so anything less would be a cap per pooled
// chain, which is no cap at all and fails by working. See EhrTraffic.
builder.Services.AddSingleton(_ => EhrTraffic.Limiter());
builder.Services.AddTransient(services => new EhrTrafficHandler(
    services.GetRequiredService<ConcurrencyLimiter>(),
    EhrTraffic.MaxWait
));

// The default timeout is 100 seconds, a long time to hold a request open for someone who
// only had to type a URL. On the defaults rather than each registration, so a client added
// later cannot forget it or the limiter.
builder.Services.ConfigureHttpClientDefaults(http =>
    http.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
        .AddHttpMessageHandler<EhrTrafficHandler>()
);
builder.Services.AddScoped<SmartLaunch>();
builder.Services.AddScoped<FhirClients>();
builder.Services.AddScoped<Chart>();
builder.Services.AddScoped<Jwks>();

// What a launch leaves behind, and the only thing here that keeps any of it: everything
// else this app holds is a credential or patient data, and stays in memory on purpose.
builder.Services.AddDbContext<AccessLogContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("AccessLog"))
);
builder.Services.AddScoped<AccessLog>();

// The learn walkthrough's exchange is a real form post, so antiforgery is live and these
// keys sign its token. Unpersisted, a container mints a ring at every boot and a reader
// sitting on step 4 across a deploy fails the exchange with a message naming nothing useful.
// Its own setting rather than the access log's directory: that one is a connection string,
// and "Data Source=app.db" names no directory to take.
builder
    .Services.AddDataProtection()
    .PersistKeysToFileSystem(
        new DirectoryInfo(builder.Configuration["DataProtection:KeyRing"] ?? "keys")
    );

// The sentence a failed launch leaves for /error travels in this cookie, and that sentence
// can name the patient a reader was looking at. So it follows the session cookie's rule
// rather than the framework's default, and for the same reason: the flag comes from the
// origin the app was told, never from the scheme of a request a proxy already rewrote.
builder
    .Services.AddOptions<CookieTempDataProviderOptions>()
    .Configure<IOptions<SmartOptions>>(
        (cookie, smart) =>
            cookie.Cookie.SecurePolicy = smart.Value.IsSecure
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.None
    );

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

// Right for a single-instance demo, and briefly untrue on every deploy: Kamal waits for the
// new container's health check before stopping the old one, so for a few seconds two
// processes hold this file and one migrates while the other serves. Accepted knowingly —
// one droplet, one SQLite file, migrations that add — and recorded in docs/deploying.md.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AccessLogContext>().Database.Migrate();

app.UseExceptionHandler("/error");

// Resolved once at start-up rather than per request: the answer cannot change while the
// app is running, because it comes from configuration and not from what a proxy sent.
var securityHeaders = SecurityHeaders.For(
    app.Services.GetRequiredService<IOptions<SmartOptions>>().Value.IsSecure
);

// On every response, including the error page: the exception handler clears the response
// and re-runs the pipeline from this point, so headers set above it would be lost on
// exactly the page that renders a failure.
app.Use(
    async (http, next) =>
    {
        foreach (var (name, value) in securityHeaders)
            http.Response.Headers[name] = value;

        await next(http);
    }
);

// MapStaticAssets fingerprints and precompresses at build time; WithStaticAssets is what
// lets the layout's plain `src="~/app.js"` resolve to the fingerprinted name. Without it the
// tag still works, and quietly gives up the caching.
app.MapStaticAssets();

app.MapRazorPages().WithStaticAssets();

// The path is kamal-proxy's default, so matching it means a deployment configures no health
// check at all. Shallow on purpose: the migration above runs before the app serves, so a
// volume that is missing or unwritable has already stopped the container.
app.MapGet("/up", () => Results.Ok());

// Step 1 of the SMART EHR launch: the EHR opens this URL with the FHIR base URL (iss) and
// an opaque launch id. SmartLaunch does the protocol; this endpoint holds the launch until
// the EHR redirects back.
app.MapGet(
    "/launch",
    async (
        string? iss,
        string? launch,
        HttpContext http,
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
            : Fail(http, LaunchMessages.For(outcome));

        IResult Remember(LaunchOutcome.Prepared prepared)
        {
            cache.Remember(prepared);
            return Results.Redirect(prepared.AuthorizeUrl);
        }
    }
);

// Steps 2 and 3, and where this app becomes stateful. It renders nothing, deliberately: the
// authorization code leaves the address bar with the redirect, and a refresh stops
// re-sending a code that has already been spent.
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
            return Fail(http, LaunchMessages.For(outcome));

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
//
// The sentence travels in TempData rather than the redirect's query string: a page that
// renders whatever a URL carries is a page a stranger can put their own words on, in this
// app's voice and on its domain, with nothing in the CSP that could stop it. TempData rides
// in a cookie the data protection ring signs, and keeps the launch id and the patient out of
// browser history.
static IResult Fail(HttpContext http, string message)
{
    var tempData = http
        .RequestServices.GetRequiredService<ITempDataDictionaryFactory>()
        .GetTempData(http);

    tempData[ErrorModel.Key] = message;

    // Razor Pages has a filter that does this; a minimal endpoint has to say so.
    tempData.Save();

    return Results.Redirect("/error");
}

// Named so the integration tests can host this app with WebApplicationFactory;
// top-level statements otherwise compile to an internal Program class.
public partial class Program;
