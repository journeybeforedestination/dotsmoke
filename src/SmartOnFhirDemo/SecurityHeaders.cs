namespace SmartOnFhirDemo;

/// <summary>
/// What every response carries, as a pure function of one fact: whether readers reach this
/// app over TLS. That fact comes from the configured origin rather than from the request,
/// for the reason <see cref="SmartOptions.PublicOrigin"/> exists at all — behind a proxy
/// that terminates TLS, the request this app sees is plain HTTP.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// <c>default-src 'none'</c>, and then the three things this app actually does.
    /// <c>style-src</c> is for the single <c>&lt;style&gt;</c> block in the layout;
    /// <c>script-src 'self'</c> and <c>connect-src 'self'</c> are for
    /// <c>wwwroot/app.js</c> and the pane it fetches. Both are <c>'self'</c> rather than a
    /// hash because the script is a file rather than an inline block — nothing here is
    /// loaded from anywhere but this origin, and no relaxation admits an inline script.
    /// <c>form-action 'self'</c> leaves the EHR's redirects alone — those are top-level
    /// GETs, not form posts — and confines the walkthrough's exchange form, the only form
    /// here, to this app.
    ///
    /// <c>frame-ancestors 'none'</c> is a claim about this app rather than about SMART:
    /// real EHRs often embed a launched app in an iframe, and one that had to be
    /// embeddable would name the EHR's origin here instead. This one is launched into a
    /// tab of its own, and a page of patient data that nothing needs to frame should not
    /// be framable.
    /// </summary>
    private const string Policy =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'self'; "
        + "connect-src 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <param name="secure">Whether the app's public origin is https.</param>
    public static KeyValuePair<string, string>[] For(bool secure) =>
        secure ? [.. Always, Hsts] : [.. Always];

    private static readonly KeyValuePair<string, string>[] Always =
    [
        new("Content-Security-Policy", Policy),
        new("X-Content-Type-Options", "nosniff"),
        // Every URL after a launch names a launch id and a patient. None of that belongs
        // in a Referer header sent to the EHR, or anywhere else.
        new("Referrer-Policy", "no-referrer"),
    ];

    /// <summary>
    /// Written by hand, and off configuration rather than off the request, because both
    /// in-box helpers are wrong behind a TLS-terminating proxy and wrong silently:
    /// <c>UseHsts</c> returns early unless <c>Request.IsHttps</c>, so it would emit
    /// nothing at all, and <c>UseHttpsRedirection</c> would redirect to a scheme the proxy
    /// keeps rewriting back, which is a loop. No <c>includeSubDomains</c>: this app knows
    /// its own host and has no business speaking for its siblings.
    /// </summary>
    private static readonly KeyValuePair<string, string> Hsts = new(
        "Strict-Transport-Security",
        "max-age=31536000"
    );
}
