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
/// What came of asking for the launch a page is showing. The reasons are not for the
/// reader — every one of them means "launch this again from the EHR", which is why the
/// pages collapse them into one prompt. They are kept apart here because the access log
/// needs them apart: a token running out is time passing, and a page whose patient does
/// not match its launch is a safety violation.
/// </summary>
public abstract record LaunchResolution
{
    private LaunchResolution() { }

    public sealed record Resolved(LaunchView View) : LaunchResolution;

    /// <summary>Never issued to this browser, or long enough ago to be gone.</summary>
    public sealed record Unknown : LaunchResolution;

    /// <summary>The EHR's token has run out, and the launch went with it.</summary>
    public sealed record Expired : LaunchResolution;

    /// <summary>
    /// The page says it is showing one patient and the launch says another. This is the
    /// failure the whole session design exists to make impossible — one patient's data
    /// under another's banner — so nothing is rendered and a row is written.
    /// </summary>
    /// <param name="Claimed">The patient the page believed it was showing.</param>
    public sealed record PatientMismatch(LaunchFacts Facts, string Claimed) : LaunchResolution;
}

/// <summary>
/// The opaque id naming the browser a request came from. It is half of what names a
/// launch: <b>the cookie authenticates and the URL selects</b>. Neither is a bearer token
/// on its own — a URL sitting in browser history or a <c>Referer</c> header leaks nothing
/// without the cookie, and the cookie does not say which of a browser's launches is meant.
/// </summary>
public static class BrowserSession
{
    public const string CookieName = ".dotsmoke.sid";

    /// <summary>
    /// The browser's id, minting and setting one if the request arrived without. Called
    /// only where a launch has just completed: a session is what holds a launch, so there
    /// is no reason for one to exist before there is a launch to put in it, and a browser
    /// arriving here without a cookie is simply one that has not launched before.
    /// </summary>
    /// <param name="secure">
    /// Whether readers reach this app over TLS, taken from its configured public origin.
    /// </param>
    public static string Establish(HttpContext http, bool secure)
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
                // GET, which Lax permits and Strict drops. A first launch would survive
                // Strict, because it has no cookie to withhold and mints one here anyway.
                // A second would not: it would arrive without the first's cookie, open a
                // session of its own, and take the first launch out of reach — which is
                // the isolation this whole design exists to keep.
                SameSite = SameSiteMode.Lax,

                // Follows the app's configured origin, never this request's scheme. Behind
                // a proxy that terminates TLS the request arrives as plain http, and
                // deriving the flag from it would mark the cookie that authenticates a
                // patient summary as safe to send in clear — silently, on a public host.
                // On the README's http://localhost:5000 it is false, which is what keeps
                // those instructions working.
                Secure = secure,
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
