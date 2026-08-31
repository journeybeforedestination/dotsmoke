using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace SmartOnFhirDemo.IntegrationTests;

/// <summary>
/// The SMART Launcher encodes its simulation settings as a base64url JSON array,
/// used both as the <c>launch</c> parameter and as a segment of the issuer URL.
/// Field order mirrors the launcher's own codec.
/// </summary>
internal static class LaunchParams
{
    private const int ProviderEhrLaunch = 0;
    private const int PublicClient = 0;
    private const int PkceAlwaysRequired = 2;

    /// <param name="providerId">
    /// Who the launch is by. Not optional in practice: the app asks for <c>openid</c>, so
    /// the launcher has to put a user in the id_token, and with no provider selected it
    /// stops at its login page rather than honouring skip_login below.
    /// </param>
    /// <param name="authError">
    /// A launcher error simulation, e.g. "auth_invalid_scope". Empty for a clean launch.
    /// </param>
    /// <param name="grantedScope">
    /// What the EHR will grant, when that is deliberately less than the app asks for.
    /// Empty leaves the launcher granting whatever was requested.
    /// </param>
    public static string Encode(
        string patientId,
        string providerId,
        string authError = "",
        string grantedScope = ""
    )
    {
        object[] fields =
        [
            ProviderEhrLaunch,
            patientId,
            providerId,
            "AUTO", // encounter
            1, // skip_login  — no login screen, so no browser needed
            1, // skip_auth   — no consent screen either
            0, // sim_ehr
            grantedScope, // scope
            "", // redirect_uris — unregistered, so any is accepted
            "", // client_id
            "", // client_secret
            authError,
            "", // jwks_url
            "", // jwks
            PublicClient,
            PkceAlwaysRequired, // make the launcher verify our S256 challenge
            "", // fhir_server — the launcher's default
        ];

        return WebEncoders.Base64UrlEncode(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))
        );
    }
}
