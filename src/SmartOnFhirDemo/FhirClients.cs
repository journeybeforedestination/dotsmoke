using System.Net.Http.Headers;
using Hl7.Fhir.Rest;

namespace SmartOnFhirDemo;

/// <summary>
/// Builds the FHIR client a launch reads through. Two things have to be true of every one,
/// and neither is a caller's to remember: it presents this launch's access token, and every
/// request it makes is written to the access log against this launch.
/// </summary>
public sealed class FhirClients(
    IHttpMessageHandlerFactory handlers,
    AccessLog log,
    TimeProvider clock
)
{
    /// <summary>
    /// The named client every FHIR read goes through. Named so its handler can be taken
    /// from the factory's pool and wrapped per launch.
    /// </summary>
    public const string Name = "fhir";

    /// <param name="verifyVersion">
    /// Whether to make Firely fetch the server's CapabilityStatement first. Worth it once,
    /// when a launch is established and nothing is known about the server yet; not worth a
    /// round trip and a log row on every follow-up read against the same server.
    /// </param>
    public LaunchFhirClient Open(LaunchContext context, bool verifyVersion = false)
    {
        // The inner handler comes from the factory's pool; the outer one is built here,
        // per launch, because that is the only way it can be told which launch it is for.
        // See AccessLogHandler for why resolving that from DI would be a bug.
        var audited = new AccessLogHandler(context, log, clock)
        {
            InnerHandler = handlers.CreateHandler(Name),
        };

        // disposeHandler: false — the inner handler belongs to the pool, and disposing it
        // would take it down for every other launch sharing it.
        var http = new HttpClient(audited, disposeHandler: false);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            context.AccessToken
        );

        return new LaunchFhirClient(
            new FhirClient(
                context.Iss,
                http,
                new FhirClientSettings
                {
                    PreferredFormat = ResourceFormat.Json,
                    VerifyFhirVersion = verifyVersion,
                }
            ),
            http
        );
    }
}

/// <summary>
/// A <see cref="FhirClient"/> and the <see cref="HttpClient"/> underneath it, disposed
/// together. Firely's documentation is explicit that an injected HttpClient is the
/// caller's to dispose, and a leak there would be silent.
/// </summary>
public sealed class LaunchFhirClient(FhirClient fhir, HttpClient http) : IDisposable
{
    public FhirClient Fhir { get; } = fhir;

    public void Dispose()
    {
        Fhir.Dispose();
        http.Dispose();
    }
}
