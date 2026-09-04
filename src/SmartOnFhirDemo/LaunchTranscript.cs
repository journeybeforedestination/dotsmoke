using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace SmartOnFhirDemo;

/// <summary>One labelled value, and why it is there.</summary>
public sealed record StepField(string Label, string Value, string Note = "");

/// <summary>
/// One stop on the narrated launch: what just happened, the values involved, and the
/// payload they were carried in.
/// </summary>
public sealed record LaunchStep(
    int Number,
    string Title,
    string Explanation,
    IReadOnlyList<StepField> Fields,
    string? PayloadLabel = null,
    string? Payload = null
);

/// <summary>
/// Turns the outcomes of a real launch into an explanation of it. Pure: what it is handed
/// has already had its credentials removed by <see cref="SmartLaunch"/>. The prose lives
/// here rather than in the pages so it can be read, reviewed and tested in one place.
///
/// A note is one sentence — a paragraph per field buries the value it is meant to be
/// explaining. Step 4's code is the one deliberate exception.
/// </summary>
public static class LaunchTranscript
{
    /// <summary>
    /// The stops in order, as the progress row names them. One word each, because they all
    /// have to fit across the page; the full titles are on the steps themselves. A step's
    /// number is its position here.
    /// </summary>
    public static readonly IReadOnlyList<string> StepLabels =
    [
        "Launch",
        "Discovery",
        "Authorize",
        "Code",
        "Token",
        "Session",
        "Identity",
        "App",
    ];

    /// <summary>
    /// Everything that happens before the browser leaves for the EHR: the two parameters
    /// that arrived, the document discovery fetched, and the request about to be made.
    /// </summary>
    public static IReadOnlyList<LaunchStep> BeforeTheRedirect(LaunchOutcome.Prepared prepared)
    {
        var authorize = Query(prepared.AuthorizeUrl);

        return
        [
            new LaunchStep(
                1,
                "What the EHR sent",
                "The EHR opened this URL in your browser. Two query parameters are the whole of "
                    + "what it told the app.",
                [
                    new StepField(
                        "iss",
                        prepared.Session.Iss,
                        "The EHR's FHIR base URL, checked against an allowlist before the app "
                            + "makes a single request to it."
                    ),
                    new StepField(
                        "launch",
                        Abbreviate(authorize["launch"]),
                        "An opaque handle to the EHR's session — which patient, which encounter, "
                            + "which user — handed straight back untouched."
                    ),
                ]
            ),
            new LaunchStep(
                2,
                "What the app discovered",
                "The app is not configured with this EHR's OAuth endpoints. SMART puts them at a "
                    + "fixed path beneath the FHIR base URL, so one request finds them.",
                [
                    new StepField(
                        "GET",
                        prepared.WellKnownUrl,
                        "The only URL the app can derive without being told anything further."
                    ),
                    new StepField(
                        "authorization_endpoint",
                        Published(prepared, "authorization_endpoint"),
                        "Where the browser is sent next, to log in and confirm the launch."
                    ),
                    new StepField(
                        "token_endpoint",
                        Published(prepared, "token_endpoint"),
                        "Where the app will trade the authorization code for an access token, "
                            + "server to server."
                    ),
                ],
                "The full SMART configuration the EHR published",
                Pretty(prepared.ConfigurationJson)
            ),
            new LaunchStep(
                3,
                "What the app is about to send",
                "An authorization request, built from what discovery returned. Pressing continue "
                    + "sends your browser to the EHR with exactly these parameters.",
                [
                    Param(
                        authorize,
                        "response_type",
                        "The authorization code flow: the browser carries only a short-lived code, "
                            + "never the token."
                    ),
                    Param(
                        authorize,
                        "client_id",
                        "How this app identifies itself — public, and not what proves anything."
                    ),
                    Param(
                        authorize,
                        "redirect_uri",
                        "Where the EHR will send your browser back, checked against what was registered."
                    ),
                    Param(
                        authorize,
                        "launch",
                        note: "The EHR's handle from step 1, returned unchanged.",
                        abbreviate: true
                    ),
                    Param(
                        authorize,
                        "scope",
                        "What the app is asking for — the EHR may grant less."
                    ),
                    Param(
                        authorize,
                        "state",
                        note: "32 random bytes tying the callback back to this launch, and the "
                            + "defence against a forged one.",
                        abbreviate: true
                    ),
                    Param(
                        authorize,
                        "aud",
                        "The FHIR server this token is for, so a leaked one cannot be replayed "
                            + "against a different EHR."
                    ),
                    Param(
                        authorize,
                        "code_challenge",
                        note: "The SHA-256 of a secret verifier that never leaves this server, and "
                            + "that redeeming the code will require.",
                        abbreviate: true
                    ),
                    Param(
                        authorize,
                        "code_challenge_method",
                        "The hash, never 'plain', which would protect nothing from anyone who can "
                            + "see the request."
                    ),
                ],
                "The full authorization URL",
                Wrap(prepared.AuthorizeUrl)
            ),
        ];
    }

    /// <summary>The EHR has sent the browser back with a code that has not been spent yet.</summary>
    public static LaunchStep TheCodeCameBack(string code, string state, LaunchState launch) =>
        new(
            4,
            "The EHR sent your browser back",
            "You have been through the EHR, and it has redirected back with an authorization "
                + "code. Nothing has been exchanged yet.",
            [
                // The one note left at length: this page exists to make the point, and the
                // point does not survive being cut to a clause.
                new StepField(
                    "code",
                    Abbreviate(code),
                    "Live and unspent right now, and plainly visible in your address bar. It is "
                        + "still not enough on its own: redeeming it also takes the PKCE verifier, "
                        + "which never left this server. That is precisely what PKCE buys — a code "
                        + "that leaks is a code that cannot be used."
                ),
                new StepField(
                    "state",
                    Abbreviate(state),
                    "Matched against the launch in flight — no match, no exchange."
                ),
                new StepField(
                    "POST",
                    launch.TokenEndpoint,
                    "The token endpoint from step 2, called from the server rather than your browser."
                ),
            ],
            "The form body about to be POSTed",
            string.Join(
                "\n",
                [
                    "grant_type    = authorization_code",
                    $"code          = {Abbreviate(code)}",
                    $"redirect_uri  = {launch.RedirectUri}",
                    $"code_verifier = {Smart.Withheld}   ← the secret half of the code_challenge above",
                    "client_id     = the same public id sent in step 3",
                ]
            )
        );

    /// <summary>What came back from the token endpoint, minus the credential.</summary>
    public static LaunchStep TheTokenResponse(CallbackOutcome.Completed completed)
    {
        var token = completed.Token;

        return new LaunchStep(
            5,
            "What the token endpoint returned",
            "The exchange succeeded. The EHR checked the code and the verifier, and issued a "
                + "token along with the context the launch was carrying.",
            [
                new StepField(
                    "access_token",
                    Smart.Withheld,
                    "A bearer credential — whoever holds it is this app, as far as the FHIR "
                        + "server is concerned."
                ),
                new StepField(
                    "token_type",
                    token.TokenType ?? "—",
                    "Always Bearer in SMART, and how the token is presented on the next request."
                ),
                new StepField(
                    "expires_in",
                    token.ExpiresIn is { } seconds ? $"{seconds} seconds" : "—",
                    "How long the token would remain valid."
                ),
                new StepField(
                    "scope",
                    token.Scope ?? "—",
                    "What the EHR actually granted, which can be narrower than step 3 asked for."
                ),
                new StepField(
                    "patient",
                    token.Patient ?? "—",
                    "The launch context resolved: the opaque handle from step 1 is now a FHIR id "
                        + "the app can read."
                ),
                new StepField(
                    "encounter",
                    token.Encounter ?? "—",
                    "The other context an EHR may pass, which this app does not use."
                ),
                new StepField(
                    "need_patient_banner",
                    token.NeedPatientBanner?.ToString() ?? "—",
                    "Whether the EHR already shows a patient banner, so an embedded app does not "
                        + "draw a second one."
                ),
                new StepField(
                    "smart_style_url",
                    token.SmartStyleUrl ?? "—",
                    "A stylesheet of the EHR's colours, so an embedded app can look like part of it."
                ),
            ],
            "The token response, with credentials removed",
            Pretty(completed.TokenJson)
        );
    }

    /// <summary>
    /// What the app did with the token, which is the step that exists because the app
    /// became stateful. Given <see cref="LaunchFacts"/> rather than a
    /// <see cref="LaunchContext"/>: this file is handed nothing that names a credential,
    /// and the point of the step is that the reader cannot be shown one either.
    /// </summary>
    public static LaunchStep TheSessionItStarts(LaunchFacts facts) =>
        new(
            6,
            "The session this launch opened",
            "The token has to outlive the request that fetched it now — the summary is a page "
                + "you can come back to, and it reads on from there. So rather than being spent "
                + "and dropped, this launch was filed against your browser, and its name put in "
                + "the URL.",
            [
                new StepField(
                    $"Set-Cookie: {BrowserSession.CookieName}",
                    Smart.Withheld,
                    "HttpOnly, so no script on this page can read it either — which is the whole "
                        + "of what HttpOnly buys."
                ),
                new StepField(
                    "SameSite",
                    "Lax",
                    "Load-bearing: Strict withholds cookies on the EHR's redirect back, so a "
                        + "second launch could not join this session."
                ),
                new StepField(
                    "launch id",
                    Abbreviate(facts.LaunchId),
                    "Minted here, not reused from step 3's state — that was sent to the EHR and "
                        + "sits in its logs."
                ),
                new StepField(
                    "patient",
                    facts.PatientId,
                    "Carried beside the launch id so every page says which patient it believes "
                        + "it shows, and can be corrected."
                ),
                new StepField(
                    "expires",
                    $"{facts.ExpiresAt:u}",
                    "The launch dies with the token it holds; expires_in from step 5 is what "
                        + "sets this."
                ),
            ],
            "What the app now holds, and what names it",
            string.Join(
                "\n",
                [
                    $"context:{{cookie}}:{{launch id}}",
                    "    iss           = " + facts.IssuerOrigin,
                    "    patient       = " + facts.PatientId,
                    "    fhirUser      = " + (facts.FhirUser ?? "(absent)"),
                    $"    expires       = {facts.ExpiresAt:u}",
                    $"    access_token  = {Smart.Withheld}",
                    "",
                    "The cookie authenticates and the URL selects, and neither is enough alone.",
                    "A cookie is per browser; a launch is per patient. Keyed on the cookie",
                    "alone, a second tab launching a second patient would overwrite the first —",
                    "and the first tab would then render one patient's data under the other's",
                    "banner, with no error anywhere.",
                ]
            )
        );

    /// <summary>
    /// Who was driving, and how much of that the app is willing to believe. This is the
    /// step the whole openid/fhirUser exchange exists for.
    /// </summary>
    public static LaunchStep WhoLaunchedThis(CallbackOutcome.Completed completed) =>
        completed.Identity is not { } claims
            ? new LaunchStep(
                7,
                "Who launched this app",
                "Nobody, as far as this launch can prove. An app learns who is driving it from "
                    + "an id_token, and this launch has none it can trust.",
                [
                    new StepField(
                        "id_token",
                        Smart.Withheld,
                        completed.IdentityUnavailable
                            ?? "The EHR returned no id_token for this launch."
                    ),
                ]
            )
            : new LaunchStep(
                7,
                "Who launched this app",
                "The token response carried an id_token as well as an access token. The access "
                    + "token says what may be read; this says who is reading.",
                [
                    new StepField(
                        "signature",
                        "verified",
                        "Checked against the keys the EHR publishes, which discovery named in step 2."
                    ),
                    new StepField(
                        "iss",
                        claims.Issuer,
                        "Matched against the issuer discovery published, not the iss the launch "
                            + "arrived with."
                    ),
                    new StepField(
                        "aud",
                        claims.Audience,
                        "This app's client_id — a token minted for a different app is refused here."
                    ),
                    new StepField(
                        "exp",
                        $"{claims.ExpiresAt:u}",
                        "Checked against the clock, because everything above is only true at a moment."
                    ),
                    new StepField(
                        "sub",
                        Abbreviate(claims.Subject),
                        "The user's stable, opaque identifier at this EHR — usable as a key, not "
                            + "as a name."
                    ),
                    new StepField(
                        "fhirUser",
                        claims.FhirUser ?? "—",
                        "A reference to the user as a FHIR resource, which is what turns an "
                            + "identity into something readable."
                    ),
                    WhoTheyAre(completed),
                ],
                "The id_token's claims, decoded — the token itself is withheld",
                string.Join(
                    "\n",
                    [
                        $"iss      = {claims.Issuer}",
                        $"aud      = {claims.Audience}",
                        $"sub      = {claims.Subject}",
                        $"fhirUser = {claims.FhirUser ?? "(absent)"}",
                        $"iat      = {claims.IssuedAt:u}",
                        $"exp      = {claims.ExpiresAt:u}",
                    ]
                )
            );

    /// <summary>The result of following the fhirUser reference, whichever way it went.</summary>
    private static StepField WhoTheyAre(CallbackOutcome.Completed completed) =>
        completed.User is { } user
            ? new StepField(
                "the user",
                user.Name ?? $"an unnamed {user.ResourceType}",
                $"Read back as a {user.ResourceType} with the same access token, which is what "
                    + "the user/Practitioner.read scope was asked for in step 3."
            )
            : new StepField(
                "the user",
                "—",
                completed.UserUnavailable
                    ?? "The id_token named nobody, so there was nothing to read."
            );

    /// <summary>
    /// The last stop, and the only one that annotates nothing: what is left is to see what
    /// the handshake bought, so this step is a sentence and the page below it is the product.
    /// </summary>
    public static LaunchStep TheApp() =>
        new(
            8,
            "The launch is done",
            "The app now holds an access token, filed against your browser's session — it is "
                + "authorized, and can go on reading this patient from the EHR for as long as the "
                + "token lasts. Everything below is the app itself, with nothing explained.",
            []
        );

    // ---- Helpers ----------------------------------------------------------

    private static StepField Param(
        Dictionary<string, string> query,
        string name,
        string note = "",
        bool abbreviate = false
    ) => new(name, abbreviate ? Abbreviate(query[name]) : query[name], note);

    /// <summary>Reads a value straight out of the document discovery returned, to show it as published.</summary>
    private static string Published(LaunchOutcome.Prepared prepared, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(prepared.ConfigurationJson);
            return document.RootElement.TryGetProperty(name, out var value)
                ? value.GetString() ?? "—"
                : "—";
        }
        catch (JsonException)
        {
            return "—";
        }
    }

    private static Dictionary<string, string> Query(string url) =>
        QueryHelpers
            .ParseQuery(new Uri(url).Query)
            .ToDictionary(p => p.Key, p => p.Value.ToString());

    /// <summary>
    /// Enough of an opaque value to recognise it, not enough to be it. Applied to the
    /// launch handle, the state, the challenge and the code — all of which are visible
    /// in the address bar anyway, and none of which a page should invite a screenshot of.
    /// </summary>
    private static string Abbreviate(string? value) =>
        value is null || value.Length <= 24
            ? value ?? "—"
            : $"{value[..8]}…{value[^4..]}  ({value.Length} characters)";

    /// <summary>
    /// Held once rather than built per call: JsonSerializerOptions caches the converters
    /// it resolves on first use, and a fresh instance each time throws that cache away.
    /// </summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Pretty(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, Indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>Breaks a long URL onto one line per query parameter, so it can be read.</summary>
    private static string Wrap(string url) => url.Replace("?", "\n  ?").Replace("&", "\n  &");
}
