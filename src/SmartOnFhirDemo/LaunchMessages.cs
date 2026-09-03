using System.Diagnostics;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;

namespace SmartOnFhirDemo;

/// <summary>
/// What to tell the user when a launch does not reach a page. The plain launch and the
/// narrated one fail identically — they differ only in what they do when they succeed —
/// so the mapping lives here rather than in each of them. It also keeps the exhaustiveness
/// check in one place: a new outcome breaks this switch, instead of quietly reaching a
/// caller that has not been taught about it.
/// </summary>
public static class LaunchMessages
{
    public const string MissingLaunchParameters =
        "This URL is meant to be opened by an EHR: both 'iss' and 'launch' query parameters are required.";

    /// <summary>
    /// The rejected issuer is deliberately absent: it is attacker-controlled, and belongs
    /// in the log rather than on a page.
    /// </summary>
    public const string UntrustedIssuer =
        "This app is not registered to launch from that EHR. Add its issuer to Smart:TrustedIssuers to allow it.";

    public const string MissingCallbackParameters =
        "Missing 'code' or 'state'. Start the launch from the EHR rather than opening this URL directly.";

    public const string UnknownLaunch =
        "This launch has expired or was already completed. Start a new launch from the EHR.";

    /// <summary>
    /// What a summary says when its launch will not resolve. Expiry, an unknown launch and
    /// a page whose patient does not match its launch all land here: they are different
    /// events, but there is nothing a reader can do differently about any of them. The
    /// distinction is kept where it can be acted on, in the access log's outcome.
    /// </summary>
    public static string Relaunch(string? patientId) =>
        string.IsNullOrEmpty(patientId)
            ? UnknownLaunch
            : $"This launch is no longer open. Start a new launch for patient {patientId} "
                + "from the EHR.";

    /// <summary>
    /// The one line the summary pages carry about who is driving the launch. A name when
    /// the app could establish one, and otherwise the reason it could not — never silence,
    /// because "no name shown" and "nobody was named" are worth telling apart.
    /// </summary>
    public static string WhoLaunchedIt(CallbackOutcome.Completed completed) =>
        completed switch
        {
            { User: { Name: { } name, ResourceType: var type } } => $"Launched by {name} ({type}).",
            { User: { ResourceType: var type } } => $"Launched by an unnamed {type}.",
            { UserUnavailable: { } why } => why,
            { IdentityUnavailable: { } why } => why,
            _ => "The EHR named nobody in the id_token it returned.",
        };

    /// <summary>
    /// What an OperationOutcome actually says, flattened to a sentence. FHIR servers put
    /// the useful part in any of three places, and an app that reads only one of them
    /// reports "an error occurred" against a server that said exactly what was wrong.
    /// </summary>
    public static string? Describe(OperationOutcome? outcome) =>
        outcome?.Issue is { Count: > 0 } issues
            ? string.Join(
                "; ",
                issues.Select(i => i.Details?.Text ?? i.Diagnostics ?? i.Code.GetLiteral())
            )
            : null;

    public static string AuthorizationDenied(string reason) =>
        $"The EHR refused the authorization request: {Excerpt(reason)}";

    /// <summary>
    /// As much of what another server said as is worth repeating. These sentences are this
    /// app's, but the reasons inside some of them are not: an error body, an
    /// <c>error_description</c> and an <c>OperationOutcome</c> are all written by whoever
    /// answered. A server that replies to a failed token exchange with an HTML page should
    /// not get to put a page on this one.
    /// </summary>
    private static string Excerpt(string reason) =>
        reason.Length <= 200 ? reason : reason[..200] + "…";

    /// <summary>
    /// Why a panel is showing no rows. Every one of these is a sentence rather than an
    /// empty list, because "this patient has no conditions recorded" and "the EHR would
    /// not tell us" look identical otherwise and mean opposite things.
    /// </summary>
    public static string For(ChartOutcome outcome) =>
        outcome switch
        {
            ChartOutcome.Read(var panel, var entries) =>
                $"{entries.Count} {panel.Title.ToLowerInvariant()}.",

            ChartOutcome.Empty(var panel) =>
                $"The EHR has no {panel.Title.ToLowerInvariant()} recorded for this patient.",

            ChartOutcome.Denied(var panel, var status) =>
                $"The EHR would not return {panel.Title.ToLowerInvariant()} ({status}). "
                    + "Asking for a scope does not oblige an EHR to grant it.",

            // No status because nothing was sent. AccessLogEntry already draws this
            // distinction for the same reason; a rendered 0 would read as nonsense.
            ChartOutcome.Unavailable(var panel, null, var reason) =>
                $"The EHR could not be reached for {panel.Title.ToLowerInvariant()}: {reason}",

            ChartOutcome.Unavailable(var panel, var status, var reason) =>
                $"The EHR returned {status} for {panel.Title.ToLowerInvariant()}: {reason}",

            ChartOutcome.LaunchGone => UnknownLaunch,

            _ => throw new UnreachableException($"{outcome.GetType().Name} is not an outcome."),
        };

    public static string For(LaunchOutcome outcome) =>
        outcome switch
        {
            LaunchOutcome.MissingParameters => MissingLaunchParameters,

            LaunchOutcome.UntrustedIssuer => UntrustedIssuer,

            LaunchOutcome.DiscoveryFailed(var wellKnown, var reason) =>
                $"Could not read the SMART configuration from {wellKnown} — {Excerpt(reason)}",

            _ => throw new UnreachableException($"{outcome.GetType().Name} is not a failure."),
        };

    public static string For(CallbackOutcome outcome) =>
        outcome switch
        {
            CallbackOutcome.AuthorizationDenied(var reason) => AuthorizationDenied(reason),

            CallbackOutcome.MissingParameters => MissingCallbackParameters,

            CallbackOutcome.UnknownLaunch => UnknownLaunch,

            CallbackOutcome.TokenExchangeFailed(var status, var reason) =>
                $"Token exchange failed ({status}): {Excerpt(reason)}",

            CallbackOutcome.TokenEndpointUnreachable(var reason) =>
                $"The EHR's token endpoint did not answer: {Excerpt(reason)}",

            CallbackOutcome.NoAccessToken => "The token endpoint returned no access token.",

            CallbackOutcome.NoPatientContext =>
                "The token response carried no patient context. Use a Provider EHR Launch with a patient selected.",

            CallbackOutcome.PatientNotFound(var iss, var patientId) =>
                $"Patient/{patientId} was not found on {iss}.",

            CallbackOutcome.PatientReadFailed(var status, var reason) =>
                $"The FHIR server returned {status}: {Excerpt(reason)}",

            CallbackOutcome.IncompatibleFhirVersion(var iss, var reason) =>
                $"The FHIR server at {iss} is not compatible with FHIR {ModelInfo.Version}: {reason}",

            _ => throw new UnreachableException($"{outcome.GetType().Name} is not a failure."),
        };
}
