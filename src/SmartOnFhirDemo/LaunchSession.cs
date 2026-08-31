namespace SmartOnFhirDemo;

/// <summary>
/// A launch that completed, as the code that makes requests for it needs it — the access
/// token included. This is the only type in the app that names a live credential, and it
/// reaches the cache and the FHIR-reading service and nothing else. Everything above them
/// takes <see cref="LaunchFacts"/>.
/// </summary>
/// <param name="LaunchId">
/// Opaque, and freshly minted at the exchange rather than reused from the OAuth
/// <c>state</c>: that value was sent to the EHR and sits in its logs, and it keys a launch
/// in flight, which is a different lifetime from one that has finished.
/// </param>
/// <param name="IssuerOrigin">The EHR, collapsed the way the access log files it.</param>
/// <param name="ExpiresAt">
/// When the EHR said the token stops being good for anything. The launch expires with it:
/// holding context past the credential it depends on only defers the failure.
/// </param>
public sealed record LaunchContext(
    string LaunchId,
    string Iss,
    string IssuerOrigin,
    string PatientId,
    string? FhirUser,
    DateTimeOffset ExpiresAt,
    string AccessToken
)
{
    /// <summary>The same launch with the credential taken off. What anything higher up gets.</summary>
    public LaunchFacts Facts => new(LaunchId, IssuerOrigin, PatientId, FhirUser, ExpiresAt);
}

/// <summary>
/// An established launch as a page model may know it. The projection is the guarantee:
/// a page cannot leak a token it was never handed, which beats a page that remembers not
/// to print one.
/// </summary>
public sealed record LaunchFacts(
    string LaunchId,
    string IssuerOrigin,
    string PatientId,
    string? FhirUser,
    DateTimeOffset ExpiresAt
);

/// <summary>What a page resolves a launch to: its facts, and the account of it to render.</summary>
public sealed record LaunchView(LaunchFacts Facts, CallbackOutcome.Completed Rendered);

/// <summary>
/// The opaque id naming the browser a request came from. It is half of what names a
/// launch: <b>the cookie authenticates and the URL selects</b>. Neither is a bearer token
/// on its own — a URL sitting in browser history or a <c>Referer</c> header leaks nothing
/// without the cookie, and the cookie does not say which of a browser's launches is meant.
/// </summary>
public static class BrowserSession
{
    public const string CookieName = ".dotsmoke.sid";

    /// <summary>The browser's id, minting and setting one if the request arrived without.</summary>
    public static string Establish(HttpContext http)
    {
        if (Current(http) is { } established)
            return established;

        var sid = Smart.NewOpaqueId();

        http.Response.Cookies.Append(
            CookieName,
            sid,
            new CookieOptions
            {
                HttpOnly = true,

                // Lax, and this is load-bearing rather than a default. The EHR sends the
                // browser back to /callback from its own origin — a cross-site top-level
                // GET, which Lax permits and Strict drops. Under Strict the cookie would
                // not arrive and every launch would end as an unknown one.
                SameSite = SameSiteMode.Lax,

                // The one place this demo knowingly relaxes: the README has you run it on
                // http://localhost:5000, and an unconditional Secure would break those
                // instructions with a failure that names nothing.
                Secure = http.Request.IsHttps,
            }
        );

        // So a second call in the same request answers with the id that is on its way to
        // the browser rather than minting another.
        http.Items[CookieName] = sid;
        return sid;
    }

    /// <summary>The browser's id, or null if it has none yet.</summary>
    public static string? Current(HttpContext http) =>
        http.Items.TryGetValue(CookieName, out var pending) && pending is string issued ? issued
        : http.Request.Cookies.TryGetValue(CookieName, out var cookie)
        && !string.IsNullOrEmpty(cookie)
            ? cookie
        : null;
}
