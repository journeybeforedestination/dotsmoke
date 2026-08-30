namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// A SMART launch is a redirect chain that crosses between two servers: the app
/// under test, hosted in memory, and the launcher, running in a container. This
/// dispatches each hop to whichever of the two owns the host, and follows the
/// redirects itself, so a test can express a whole launch as a single GET.
/// </summary>
internal sealed class LaunchChainHandler(HttpMessageHandler appHandler, string appHost)
    : HttpMessageHandler
{
    private const int MaxHops = 10;

    private readonly HttpMessageInvoker _app = new(appHandler);

    // Redirects must come back here to be routed, not be followed inside one handler:
    // the launcher redirects to the app, which only this class knows how to reach.
    private readonly HttpMessageInvoker _network = new(
        new SocketsHttpHandler { AllowAutoRedirect = false }
    );

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var current = request;

        for (var hop = 0; hop < MaxHops; hop++)
        {
            var target = current.RequestUri!.Host == appHost ? _app : _network;
            var response = await target.SendAsync(current, cancellationToken);

            if (response.Headers.Location is not { } location || !IsRedirect(response.StatusCode))
                return response;

            current = new HttpRequestMessage(HttpMethod.Get, new Uri(current.RequestUri, location));
            response.Dispose();
        }

        throw new InvalidOperationException(
            $"The launch did not settle within {MaxHops} redirects, starting from {request.RequestUri}."
        );
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        (int)status is >= 300 and < 400;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.Dispose();
            _network.Dispose();
        }

        base.Dispose(disposing);
    }
}
