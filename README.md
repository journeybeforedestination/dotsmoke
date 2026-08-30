# dotsmoke

A minimal SMART on FHIR app that handles a standard EHR launch end to end, then
renders a short summary of the patient in context — and, on a second launch URL,
walks you through the same launch a step at a time while it happens.

Built as a proof of concept against the public
[SMART App Launcher](https://launch.smarthealthit.org/), on .NET 10, ASP.NET Core
Razor Pages, and the [Firely SDK](https://github.com/FirelyTeam/firely-net-sdk).

## The launch flow

```
SMART Launcher ──GET /launch?iss=…&launch=…──▶ app
  app ──GET {iss}/.well-known/smart-configuration──▶ authorize + token endpoints
  app ──302──▶ {authorize}?…&aud={iss}&launch=…&code_challenge=…
  launcher (provider login → patient/consent) ──302──▶ app /callback?code=…&state=…
  app ──POST {token}──▶ { access_token, patient }
  app ──GET {iss}/Patient/{id}  Bearer──▶ Patient
  app  render summary; access token discarded
```

## The same launch, narrated

`/learn` is the second launch URL. It runs the identical protocol against the identical
EHR — same trust check, same discovery, same PKCE, same token exchange — but stops where
the plain launch redirects, and explains what was exchanged before going on.

```
SMART Launcher ──GET /learn?iss=…&launch=…──▶ app
  app  discovery, PKCE, authorize URL          ──▶ ① what the EHR sent
                                                   ② what discovery returned
                                                   ③ what is about to be sent   [continue]
  launcher (provider login → patient/consent) ──302──▶ app /learn/callback?code=…&state=…
  app                                          ──▶ ④ the code, still unspent    [exchange]
  app ──POST {token}──▶ token used once, discarded, transcript kept
  app ──GET {iss}/Patient/{id}  Bearer──▶ Patient
  app  ──302──▶ /learn/token   ──▶ ⑤ what the token endpoint returned           [continue]
               /learn/patient  ──▶ ⑥ the FHIR read, and the summary
```

Three of those are real pauses in a real launch, not a replay. Steps ⑤ and ⑥ read back a
transcript of the exchange that the token was already removed from, which is what lets
them be ordinary linkable pages without the launch holding a credential open.

What the pages never show: the PKCE verifier, the access token, and — when the issuer is
refused — the issuer. What they do show, because you learn nothing otherwise: the granted
scope, the resolved patient context, the full SMART configuration the EHR published, the
token response with its credentials replaced, and enough of the authorization code to
recognise it. The code is live and unspent at step ④, which is exactly the point being
made there: without the verifier, which never leaves the server, it cannot be redeemed.

Pausing at step ④ works because the SMART App Launcher issues codes that live five
minutes. The specification expects "around one minute", so the same pause would often
fail against a production EHR — which is why `/launch` does not take it.

## Running it

```bash
dotnet run --project src/SmartOnFhirDemo
```

Then at [launch.smarthealthit.org](https://launch.smarthealthit.org/):

| Field | Value |
| --- | --- |
| Launch Type | Provider EHR Launch |
| FHIR Version | R4 |
| App's Launch URL | `http://localhost:5000/learn` or `http://localhost:5000/launch` |
| Client ID, Redirect URIs | leave blank |

Pick a patient, press **Launch**. `/learn` walks you through the handshake; `/launch`
does the same thing without stopping and lands on the summary.

To launch from a different EHR, add its issuer to `Smart:TrustedIssuers` in
`appsettings.json` — the app refuses launches from anywhere not on that list:

```json
"Smart": {
  "TrustedIssuers": [ "https://launch.smarthealthit.org", "https://ehr.example" ]
}
```

## Tests

```bash
dotnet test                                              # everything
dotnet test --project tests/SmartOnFhirDemo.UnitTests    # just the fast ones
```

The unit tests cover the pure code — the FHIR projection in `PatientSummary`, the
protocol helpers in `Smart` — and need nothing but the SDK.

The integration tests host the app in memory and drive it through a real SMART
launch. Most of them need the [SMART App Launcher][launcher] running:

```bash
docker run -d --name smart-launcher -p 8080:80 \
  smartonfhir/smart-launcher-2@sha256:72bd3e3c682ce4c74e6dddb605d89acad7c8aae446ae38079e8dfe8455b84793

SMART_LAUNCHER_URL=http://localhost:8080 dotnet test
```

Without `SMART_LAUNCHER_URL` those tests skip and the rest still run, so
`dotnet test` stays green on a machine with no launcher. Prefix the `docker`
command with `sudo` on a host that keeps its users out of the root-equivalent
`docker` group.

The image is pinned by digest because the launcher publishes only a `latest` tag.
It proxies the public `r4.smarthealthit.org` sandbox, so these tests need an
internet connection, and they assert on the shape of the rendered summary rather
than on specific patient data — that sandbox can be reseeded.

[launcher]: https://github.com/smart-on-fhir/smart-launcher-v2

### If you wire up CI

None of this is wired up, but the constraints are known.

The integration tests reach two things outside your control. Pulling the launcher
image is free on GitHub-hosted runners, which are exempt from Docker Hub's limits
for public images; self-hosted runners are not, and share a low anonymous quota per
IP, so authenticate or mirror the image. The larger risk is the sandbox the launcher
proxies: `r4.smarthealthit.org` reports `Smile CDR 2019.08.PRE / HAPI FHIR
4.0.0-SNAPSHOT`, a 2019 pre-release, with no SLA, no status page to gate on, and no
rate-limit headers. It answers in milliseconds today, but it can be down, reseeded,
or throttled without notice.

Only reseeding constrains the tests as written, and they already assert on the shape
of the rendered summary rather than on specific patient data.

The two projects suggest their own CI shape: run the unit tests on every push — fast,
offline, gating pull requests — and the integration tests nightly and on demand,
where a red run is real signal about either the code or the sandbox rather than noise
on someone's branch.

The cheapest gate to add is not a test at all: `dotnet csharpier check .` needs no
network, no container and no sandbox, and fails in under a second.

To go hermetic, point the launcher at your own FHIR server with `FHIR_SERVER_R4`;
that touches the fixture only, not the tests. Either run `hapiproject/hapi` and seed
it, or serve a stub that answers `/metadata` with a valid R4 `CapabilityStatement` —
`VerifyFhirVersion` means the app fetches that first — along with `/Patient/{id}`.

## Formatting

[CSharpier][csharpier] owns the layout of every `.cs` and `.csproj` file here. It is
pinned in `.config/dotnet-tools.json`, so a clone gets the same version:

```bash
dotnet tool restore
dotnet csharpier format .   # apply
dotnet csharpier check .    # verify, changing nothing
```

`.editorconfig` covers what CSharpier does not: naming, `var`, pattern matching,
expression-bodied members. Those are all suggestions — nothing fails a build over
them — and they were written to describe the code that was already here rather than
to impose a house style on it. The one rule turned off outright is `IDE0055`, the
umbrella for every whitespace and new-line option, which contradicts CSharpier often
enough that leaving both on would have the two tools undoing each other.

Guard clauses stay brace-free (`csharp_prefer_braces = when_multiline`) and
`.cshtml` is formatted by hand — CSharpier has no Razor support, and neither does
`dotnet format`.

[csharpier]: https://csharpier.com/

## Design notes

- **The issuer is checked against an allowlist.** `iss` arrives as a query
  parameter and everything downstream trusts it: the app fetches that host's
  configuration, sends the user to the authorization endpoint it names, and posts
  the authorization code to its token endpoint. Unchecked, that is a server-side
  request forgery, an open redirect, and a way to harvest codes. `TrustedIssuers`
  lists the EHRs a launch may come from, compared by origin because a SMART issuer
  legitimately carries a path. An empty list trusts nobody.
- **The protocol is separate from the web layer.** `SmartLaunch` does discovery,
  the authorization request, the token exchange and the patient read, returning a
  closed set of outcomes; `/launch` and the callback page map those onto responses.
  That separation is what lets the launch be tested without a web host.
- **The OAuth handshake is hand-rolled** over `HttpClient`. SMART reveals the
  issuer only at launch time, which ASP.NET's `OpenIdConnect` middleware — built
  around a static `Authority` — fights.
- **Nothing is persisted.** Only the issuer and PKCE verifier survive the
  redirect, held in `IMemoryCache` under the OAuth `state` for five minutes. The
  access token is used once and discarded, so `/callback` renders in the same
  request that exchanges the code — and so does `/learn`'s exchange, which is why
  its later steps read a transcript rather than resume a live launch. That
  transcript is the one thing the narrated launch adds to the cache: no credential,
  but patient data, so it expires on the same five minutes and every `/learn` page
  sends `Cache-Control: no-store`.
- **Credentials are removed where they arrive, not where they render.**
  `SmartLaunch` redacts the token response and projects it into `TokenFacts` before
  returning; the access token is not a field on any outcome. A page cannot leak what
  it was never handed, which beats a page that remembers not to print it.
- **The explanation is a pure projection.** `LaunchTranscript` turns outcomes into
  ordered steps with their fields, payloads and prose. It does no I/O and reaches
  nothing but what it is given, so the narrated launch is readable and reviewable in
  one file, and the pages stay markup.
- **Firely does the FHIR work**, not just the HTTP call: FHIRPath for element
  selection, `EnumUtility` for coded display text, `Date` for partial birth
  dates, `OperationOutcome` for server errors.

## Dependencies

The app has one direct package: `Hl7.Fhir.R4` 6.4.0 (BSD-3-Clause). Everything
else is in-box ASP.NET Core.

The tests add `xunit.v3` and `Microsoft.AspNetCore.Mvc.Testing`, and nothing else
— no mocking library, no container library. `global.json` opts `dotnet test` into
Microsoft.Testing.Platform, which the .NET 10 SDK requires.

One dev-time tool, `CSharpier` 1.3.0 (MIT), is pinned in
`.config/dotnet-tools.json`. It formats the source and ships in nothing.

## License

MIT — see [LICENSE](LICENSE).
