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

    /// <param name="authError">
    /// A launcher error simulation, e.g. "auth_invalid_scope". Empty for a clean launch.
    /// </param>
    public static string Encode(string patientId, string authError = "")
    {
        object[] fields =
        [
            ProviderEhrLaunch,
            patientId,
            "", // provider
            "AUTO", // encounter
            1, // skip_login  — no login screen, so no browser needed
            1, // skip_auth   — no consent screen either
            0, // sim_ehr
            "", // scope       — the app asks for its own
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
