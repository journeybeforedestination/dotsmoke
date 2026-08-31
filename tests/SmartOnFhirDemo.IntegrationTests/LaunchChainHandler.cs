using System.Net;

namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// A SMART launch is a redirect chain that crosses between two servers: the app
/// under test, hosted in memory, and the launcher, running in a container. This
/// dispatches each hop to whichever of the two owns the host, follows the
/// redirects itself, and keeps a cookie jar, so a test can express a whole launch
/// as a single GET.
///
/// One jar per client, so two launches down one client are two tabs of one browser
/// and two clients are two browsers.
/// </summary>
internal sealed class LaunchChainHandler(HttpMessageHandler appHandler, string appHost)
    : HttpMessageHandler
{
    private const int MaxHops = 10;

    private readonly HttpMessageInvoker _app = new(appHandler);

    // Redirects must come back here to be routed, not be followed inside one handler:
    // the launcher redirects to the app, which only this class knows how to reach.
    // Cookies are handled here for the same reason — half the chain is a TestServer,
    // which has no jar of its own.
    private readonly HttpMessageInvoker _network = new(
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
    );

    private readonly CookieContainer _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var current = request;

        for (var hop = 0; hop < MaxHops; hop++)
        {
            Attach(current);

            var target = current.RequestUri!.Host == appHost ? _app : _network;
            var response = await target.SendAsync(current, cancellationToken);

            Collect(current.RequestUri!, response);

            if (response.Headers.Location is not { } location || !IsRedirect(response.StatusCode))
                return response;

            current = new HttpRequestMessage(HttpMethod.Get, new Uri(current.RequestUri, location));
            response.Dispose();
        }

        throw new InvalidOperationException(
            $"The launch did not settle within {MaxHops} redirects, starting from {request.RequestUri}."
        );
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    /// <summary>
    /// Sends what the jar holds for this URL, unless the test said what browser it is
    /// being — a test that sets its own Cookie header means it.
    /// </summary>
    private void Attach(HttpRequestMessage request)
    {
        if (
            !request.Headers.Contains("Cookie")
            && _cookies.GetCookieHeader(request.RequestUri!) is { Length: > 0 } header
        )
            request.Headers.Add("Cookie", header);
    }

    private void Collect(Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return;

        foreach (var value in values)
            _cookies.SetCookies(uri, value);
    }

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
