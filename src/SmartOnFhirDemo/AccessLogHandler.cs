namespace SmartOnFhirDemo;

/// <summary>
/// Writes an access log row for every request this app makes to an EHR.
///
/// Captured here rather than in the pages because a new read cannot forget to audit itself:
/// there is one place requests leave, and this is it. Explicit writes in the page handlers
/// would fail the way audit logs usually fail — by a call nobody remembered to add.
///
/// <b>The launch is a constructor argument, and that is load-bearing.</b>
/// <c>IHttpClientFactory</c> pools handlers for two minutes and gives each its own DI scope,
/// one that is not the request's and outlives it. A handler that resolved "the current
/// launch" from DI would be reused across incoming requests and attribute one patient's read
/// to another launch — an audit log that misattributes, which is worse than none.
/// </summary>
internal sealed class AccessLogHandler(LaunchContext launch, AccessLog log, TimeProvider clock)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // A request that never got a response reached nothing, so there is no row for a
        // connection that failed. What is logged is what an EHR actually answered.
        var response = await base.SendAsync(request, cancellationToken);

        var path = Relative(request.RequestUri);

        await log.RecordAsync(
            new AccessLogEntry(
                clock.GetUtcNow(),
                launch.LaunchId,
                launch.IssuerOrigin,
                launch.PatientId,
                launch.FhirUser,
                ResourceType(path),
                path,
                Outcome((int)response.StatusCode),
                (int)response.StatusCode
            ),
            cancellationToken
        );

        return response;
    }

    /// <summary>
    /// Where the request went, relative to this launch's FHIR base — so a row reads
    /// <c>Patient/123</c> whatever host the launch was against, and the host itself is
    /// already the row's key.
    /// </summary>
    private string Relative(Uri? url)
    {
        var absolute = url?.ToString() ?? "";
        var origin = $"{launch.Iss.TrimEnd('/')}/";

        return absolute.StartsWith(origin, StringComparison.OrdinalIgnoreCase)
            ? absolute[origin.Length..]
            : absolute;
    }

    /// <summary>The first path segment: what kind of thing was asked for.</summary>
    private static string ResourceType(string path) =>
        path.Split('/', '?') is [{ Length: > 0 } first, ..] ? first : "(none)";

    /// <summary>
    /// What the EHR's answer means, kept separate from the status it said it with. A
    /// reader of the log asks whether a read happened, not which of two 4xx codes an
    /// implementation chose.
    /// </summary>
    private static string Outcome(int status) =>
        status switch
        {
            >= 200 and < 300 => AccessOutcome.Ok,
            401 or 403 => AccessOutcome.Denied,
            404 => AccessOutcome.NotFound,
            _ => AccessOutcome.Failed,
        };
}
