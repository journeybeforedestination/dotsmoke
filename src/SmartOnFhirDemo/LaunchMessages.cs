using System.Diagnostics;
using Hl7.Fhir.Model;

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

    /// <summary>Only the narrated launch can reach this: the plain one keeps nothing to come back to.</summary>
    public const string ExpiredWalkthrough =
        "This walkthrough has expired. It is kept for five minutes after the launch completes, "
        + "and then discarded along with everything it was showing. Start a new launch from the EHR.";

    public static string AuthorizationDenied(string reason) =>
        $"The EHR refused the authorization request: {reason}";

    public static string For(LaunchOutcome outcome) =>
        outcome switch
        {
            LaunchOutcome.MissingParameters => MissingLaunchParameters,

            LaunchOutcome.UntrustedIssuer => UntrustedIssuer,

            LaunchOutcome.DiscoveryFailed(var wellKnown, var reason) =>
                $"Could not read the SMART configuration from {wellKnown} — {reason}",

            _ => throw new UnreachableException($"{outcome.GetType().Name} is not a failure."),
        };

    public static string For(CallbackOutcome outcome) =>
        outcome switch
        {
            CallbackOutcome.AuthorizationDenied(var reason) => AuthorizationDenied(reason),

            CallbackOutcome.MissingParameters => MissingCallbackParameters,

            CallbackOutcome.UnknownLaunch => UnknownLaunch,

            CallbackOutcome.TokenExchangeFailed(var status, var reason) =>
                $"Token exchange failed ({status}): {reason}",

            CallbackOutcome.NoAccessToken => "The token endpoint returned no access token.",

            CallbackOutcome.NoPatientContext =>
                "The token response carried no patient context. Use a Provider EHR Launch with a patient selected.",

            CallbackOutcome.PatientNotFound(var iss, var patientId) =>
                $"Patient/{patientId} was not found on {iss}.",

            CallbackOutcome.PatientReadFailed(var status, var reason) =>
                $"The FHIR server returned {status}: {reason}",

            CallbackOutcome.IncompatibleFhirVersion(var iss, var reason) =>
                $"The FHIR server at {iss} is not compatible with FHIR {ModelInfo.Version}: {reason}",

            _ => throw new UnreachableException($"{outcome.GetType().Name} is not a failure."),
        };
}
