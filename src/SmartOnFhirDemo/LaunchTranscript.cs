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
    string Title,
    string Explanation,
    IReadOnlyList<StepField> Fields,
    string? PayloadLabel = null,
    string? Payload = null);

/// <summary>
/// Turns the outcomes of a real launch into an explanation of it. Pure: it does no I/O
/// and reaches nothing but what it is handed, and what it is handed has already had its
/// credentials removed by <see cref="SmartLaunch"/>. The prose lives here rather than in
/// the pages so that it can be read, reviewed and tested in one place.
/// </summary>
public static class LaunchTranscript
{
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
                "1 · What the EHR sent",
                "The EHR opened this URL in your browser. Two query parameters are the whole "
                + "of what it told the app — everything else below, the app had to work out "
                + "for itself.",
                [
                    new StepField("iss", prepared.Session.Iss,
                        "The EHR's FHIR base URL. It arrived as a query parameter, so anyone can "
                        + "propose one; this app checked it against an allowlist before making a "
                        + "single request to it. Unchecked, iss is a server-side request forgery, "
                        + "an open redirect, and a way to harvest authorization codes."),
                    new StepField("launch", Abbreviate(authorize["launch"]),
                        "An opaque handle to the EHR's session — which patient, which encounter, "
                        + "which user. The app never decodes it and never needs to. It hands it "
                        + "straight back in the next request, and the EHR restores the context."),
                ]),

            new LaunchStep(
                "2 · What the app discovered",
                "The app knows nothing about this EHR's OAuth setup, and is not configured with "
                + "it. SMART puts the answer at a fixed path beneath the FHIR base URL, so one "
                + "request turns an issuer into a set of endpoints.",
                [
                    new StepField("GET", prepared.WellKnownUrl,
                        "The only URL the app can derive without being told anything further."),
                    new StepField("authorization_endpoint", Published(prepared, "authorization_endpoint"),
                        "Where the browser is sent next, to log in and confirm the launch."),
                    new StepField("token_endpoint", Published(prepared, "token_endpoint"),
                        "Where the app will trade the authorization code for an access token, "
                        + "server to server, with no browser involved."),
                ],
                "The full SMART configuration the EHR published",
                Pretty(prepared.ConfigurationJson)),

            new LaunchStep(
                "3 · What the app is about to send",
                "An authorization request, built from what discovery returned. Pressing continue "
                + "sends your browser to the EHR with exactly these parameters. Everything the "
                + "app will later need to prove is decided here.",
                [
                    Param(authorize, "response_type",
                        "The authorization code flow. The browser carries only a short-lived code; "
                        + "the token is fetched later, out of the browser's reach."),
                    Param(authorize, "client_id",
                        "How this app identifies itself. It is public — not a secret, and not "
                        + "what proves anything."),
                    Param(authorize, "redirect_uri",
                        "Where the EHR will send your browser back. The EHR checks it against what "
                        + "was registered, and the same value has to be repeated at the token call."),
                    Param(authorize, "launch", note:
                        "The EHR's handle from step 1, returned unchanged.", abbreviate: true),
                    Param(authorize, "scope",
                        "What the app is asking for. The EHR may grant less, and the app finds out "
                        + "what it actually got in the token response."),
                    Param(authorize, "state", note:
                        "32 random bytes, minted for this launch alone. It ties the callback back "
                        + "to this launch and nothing else — the defence against a forged callback.",
                        abbreviate: true),
                    Param(authorize, "aud",
                        "The FHIR server this token is for. Binding the token to one server is what "
                        + "stops a leaked one being replayed against a different EHR."),
                    Param(authorize, "code_challenge", note:
                        "The SHA-256 of a random verifier the app generated a moment ago. The "
                        + "verifier itself never leaves this server, and redeeming the code will "
                        + "require it. That is PKCE, and it is why the next step is safe.",
                        abbreviate: true),
                    Param(authorize, "code_challenge_method",
                        "The hash, never 'plain'. A plain challenge protects nothing from anyone "
                        + "who can see the request."),
                ],
                "The full authorization URL",
                Wrap(prepared.AuthorizeUrl)),
        ];
    }

    /// <summary>The EHR has sent the browser back with a code that has not been spent yet.</summary>
    public static LaunchStep TheCodeCameBack(string code, string state, LaunchState launch) =>
        new(
            "4 · The EHR sent your browser back",
            "You have been through the EHR — login, patient selection, consent, or whichever of "
            + "those it chose to show. It has redirected back to the app's redirect URI with an "
            + "authorization code. Nothing has been exchanged yet: pressing continue is what "
            + "makes the token call.",
            [
                new StepField("code", Abbreviate(code),
                    "Live and unspent right now, and plainly visible in your address bar. It is "
                    + "still not enough on its own: redeeming it also takes the PKCE verifier, "
                    + "which never left this server. That is precisely what PKCE buys — a code "
                    + "that leaks is a code that cannot be used."),
                new StepField("state", Abbreviate(state),
                    "Matched against the launch this app has in flight. No match, no exchange — "
                    + "which is how a callback the app never asked for gets refused."),
                new StepField("POST", launch.TokenEndpoint,
                    "The token endpoint discovery named in step 2. This request goes from the "
                    + "server, not from your browser."),
            ],
            "The form body about to be POSTed",
            string.Join("\n",
            [
                "grant_type    = authorization_code",
                $"code          = {Abbreviate(code)}",
                $"redirect_uri  = {launch.RedirectUri}",
                $"code_verifier = {Smart.Withheld}   ← the secret half of the code_challenge above",
                "client_id     = the same public id sent in step 3",
            ]));

    /// <summary>What came back from the token endpoint, minus the credential.</summary>
    public static LaunchStep TheTokenResponse(CallbackOutcome.Completed completed)
    {
        var token = completed.Token;

        return new LaunchStep(
            "5 · What the token endpoint returned",
            "The exchange succeeded. The EHR checked the code, checked that the verifier hashes "
            + "to the challenge from step 3, and issued an access token along with the context "
            + "the launch was carrying all along.",
            [
                new StepField("access_token", Smart.Withheld,
                    "Present, and deliberately not shown. It is a bearer credential: anyone "
                    + "holding it is, as far as the FHIR server is concerned, this app. It was "
                    + "used once — in the request on the next page — and never stored."),
                new StepField("token_type", token.TokenType ?? "—",
                    "Always Bearer in SMART. It names how the token is presented on the next request."),
                new StepField("expires_in", token.ExpiresIn is { } seconds ? $"{seconds} seconds" : "—",
                    "How long the token would remain valid. This app outlives it by rather less."),
                new StepField("scope", token.Scope ?? "—",
                    "What the EHR actually granted, which can be narrower than what step 3 asked "
                    + "for. An app is expected to cope with getting less."),
                new StepField("patient", token.Patient ?? "—",
                    "The launch context, resolved at last. The opaque launch handle from step 1 "
                    + "has become a FHIR id the app can read."),
                new StepField("encounter", token.Encounter ?? "—",
                    "The other context the EHR may pass. This app does not ask for it or use it."),
            ],
            "The token response, with credentials removed",
            Pretty(completed.TokenJson));
    }

    /// <summary>The one request all of the preceding was arranged to permit.</summary>
    public static LaunchStep ThePatientRead(CallbackOutcome.Completed completed) =>
        new(
            "6 · Reading the patient",
            "Everything up to here was arranged so that this one request could be made. It is an "
            + "ordinary FHIR read, and the access token is the only thing about it that is unusual.",
            [
                new StepField("GET", completed.PatientUrl,
                    "The FHIR base URL from step 1, and the patient id from step 5."),
                new StepField("Authorization", $"Bearer {Smart.Withheld}",
                    "The token, presented the way token_type said to. This is the whole of the "
                    + "app's claim to be allowed to read this record."),
                new StepField("Accept", "application/fhir+json",
                    "Set by the Firely client, which also asks the server for its CapabilityStatement "
                    + "first to confirm it really speaks the FHIR version this app was built against."),
            ],
            "The Patient resource, exactly as the server returned it",
            completed.RawJson);

    // ---- Helpers ----------------------------------------------------------

    private static StepField Param(
        IReadOnlyDictionary<string, string> query, string name, string note = "", bool abbreviate = false) =>
        new(name, abbreviate ? Abbreviate(query[name]) : query[name], note);

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

    private static IReadOnlyDictionary<string, string> Query(string url) =>
        QueryHelpers.ParseQuery(new Uri(url).Query)
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

    private static string Pretty(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>Breaks a long URL onto one line per query parameter, so it can be read.</summary>
    private static string Wrap(string url) =>
        url.Replace("?", "\n  ?").Replace("&", "\n  &");
}
