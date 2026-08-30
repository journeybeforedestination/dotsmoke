# dotsmoke

A minimal SMART on FHIR app that handles a standard EHR launch end to end, then
renders a short summary of the patient in context — and, on a second launch URL,
walks you through the same launch a step at a time while it happens — including who
started it, and how much of that the app is willing to believe.

Built as a proof of concept against the public
[SMART App Launcher](https://launch.smarthealthit.org/), on .NET 10, ASP.NET Core
Razor Pages, and the [Firely SDK](https://github.com/FirelyTeam/firely-net-sdk).

## The launch flow

```
SMART Launcher ──GET /launch?iss=…&launch=…──▶ app
  app ──GET {iss}/.well-known/smart-configuration──▶ authorize + token endpoints
  app ──302──▶ {authorize}?…&aud={iss}&launch=…&code_challenge=…
  launcher (provider login → patient/consent) ──302──▶ app /callback?code=…&state=…
  app ──POST {token}──▶ { access_token, id_token, patient }
  app  validate id_token against the keys at {jwks_uri}
  app ──GET {iss}/Patient/{id}   Bearer──▶ Patient
  app ──GET {iss}/{fhirUser}     Bearer──▶ Practitioner
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
  app  validate id_token; ──GET {iss}/Patient/{id} and {iss}/{fhirUser}  Bearer
  app  ──302──▶ /learn/token   ──▶ ⑤ what the token endpoint returned           [continue]
               /learn/user    ──▶ ⑥ who launched it, and how that was proved   [continue]
               /learn/patient ──▶ ⑦ the FHIR read, and the summary
```

Two of those stops happen inside a live launch rather than a replay: the first, which
holds the browser before the redirect, and step ④, which holds an authorization code that
has not been spent. Steps ⑤ to ⑦ read back a transcript of the exchange that the token was
already removed from, which is what lets them be ordinary linkable pages without the
launch holding a credential open.

What the pages never show: the PKCE verifier, the access token, and — when the issuer is
refused — the issuer. What they do show, because you learn nothing otherwise: the granted
scope, the resolved patient context, the full SMART configuration the EHR published, the
token response with its credentials replaced, the id_token's claims with the token itself
withheld, and enough of the authorization code to recognise it. The code is live and unspent at step ④, which is exactly the point being
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

`Smart:Scopes` is what the app asks each of them for. `openid fhirUser` is what makes
an EHR say who started the launch, and `user/Practitioner.read` is what lets that
person's name be read; drop either and the launch still works, and the summary simply
says nobody was named.

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

Coverage is measured the same way locally:

```bash
dotnet test --coverage --coverage-settings coverage.config \
  --coverage-output-format cobertura
./.github/coverage.sh
```

`coverage.config` excludes `obj/`, where the logging source generator's output lives.
It deliberately does not exclude the `.cshtml` files: those read 0% without a launcher
and near-100% with one, which is a true statement about which tests render them rather
than noise worth hiding.

Without `SMART_LAUNCHER_URL` those tests skip and the rest still run, so
`dotnet test` stays green on a machine with no launcher. Prefix the `docker`
command with `sudo` on a host that keeps its users out of the root-equivalent
`docker` group.

The image is pinned by digest because the launcher publishes only a `latest` tag.
It proxies the public `r4.smarthealthit.org` sandbox, so these tests need an
internet connection, and they assert on the shape of the rendered summary rather
than on specific patient data — that sandbox can be reseeded.

[launcher]: https://github.com/smart-on-fhir/smart-launcher-v2

### CI

`.github/workflows/ci.yml` runs on every push and pull request:

```bash
dotnet tool restore
dotnet csharpier check .                 # no network, no container, under a second
dotnet restore --locked-mode
dotnet build --no-restore -warnaserror
dotnet test --no-build --coverage --coverage-settings coverage.config \
  --coverage-output-format cobertura
./.github/coverage.sh                    # merge the two reports, report the rate
```

That last line runs both projects, not only the fast one. Twenty-two of the
thirty-three integration tests need no launcher — among them every untrusted-issuer
refusal, which is the app's central security property — and the eleven that do skip
themselves. The whole job stays offline.

The launcher-bound tests run in a second job, nightly and on demand, which starts the
container first. Because those tests skip themselves when `SMART_LAUNCHER_URL`
answers nothing, a container that failed to start would leave the job green while
testing nothing — so the job polls the launcher's FHIR endpoint and fails there
instead, where the cause is legible.

That job reaches two things outside your control. Pulling the launcher image is free
on GitHub-hosted runners, which are exempt from Docker Hub's limits for public
images; self-hosted runners are not, and share a low anonymous quota per IP, so
authenticate or mirror the image. The larger risk is the sandbox the launcher
proxies: `r4.smarthealthit.org` reports `Smile CDR 2019.08.PRE / HAPI FHIR
4.0.0-SNAPSHOT`, a 2019 pre-release, with no SLA, no status page to gate on, and no
rate-limit headers. It answers in milliseconds today, but it can be down, reseeded,
or throttled without notice. Only reseeding constrains the tests as written, and they
already assert on the shape of the rendered summary rather than on specific patient
data.

To go hermetic, point the launcher at your own FHIR server with `FHIR_SERVER_R4`;
that touches the fixture only, not the tests. Either run `hapiproject/hapi` and seed
it, or serve a stub that answers `/metadata` with a valid R4 `CapabilityStatement` —
`VerifyFhirVersion` means the app fetches that first — along with `/Patient/{id}`.

`.github/dependabot.yml` proposes NuGet and action updates weekly. It does not cover
`.config/dotnet-tools.json`, so the CSharpier and `dotnet-coverage` pins are bumped
by hand.

## Formatting

[CSharpier][csharpier] owns the layout of every `.cs`, `.csproj` and `.props` file
here. It is pinned in `.config/dotnet-tools.json`, so a clone gets the same version:

```bash
dotnet tool restore
dotnet csharpier format .   # apply
dotnet csharpier check .    # verify, changing nothing
```

`.editorconfig` covers what CSharpier does not: naming, `var`, pattern matching,
expression-bodied members. Those are suggestions — with one exception noted under
Analyzers, nothing fails a build over them — and they were written to describe the
code that was already here rather than to impose a house style on it. The one rule
turned off outright is `IDE0055`, the umbrella for every whitespace and new-line
option, which contradicts CSharpier often enough that leaving both on would have the
two tools undoing each other.

Guard clauses stay brace-free (`csharp_prefer_braces = when_multiline`) and
`.cshtml` is formatted by hand — CSharpier has no Razor support, and neither does
`dotnet format`.

The commit that first ran CSharpier over the whole tree is listed in
`.git-blame-ignore-revs`, so blame lands on whoever wrote a line rather than on
the reformat. GitHub applies that file by itself; locally it needs telling once:

```bash
git config blame.ignoreRevsFile .git-blame-ignore-revs
```

[csharpier]: https://csharpier.com/

## Analyzers

The .NET SDK ships Roslyn analyzers and runs a small subset of them by default.
`Directory.Build.props` turns them up:

```xml
<AnalysisLevel>10.0-recommended</AnalysisLevel>
<AnalysisModeSecurity>All</AnalysisModeSecurity>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
```

Warnings while you type, errors in CI — `dotnet build -warnaserror` is the gate.
Nothing extra is installed to get this: no third-party analyzer package. The
security category runs at maximum because on this code it reports nothing, which
is worth having for free in an app that fetches a URL handed to it in a query
parameter.

`10.0-recommended` pins the rule set to the .NET 10 SDK's idea of *recommended*
rather than to whichever SDK is installed, so a newer SDK cannot fail a build that
nothing here changed. Bumping that number is a deliberate act, like the CSharpier
version.

The dependency audit comes along for free: NuGet checks the resolved graph against
its advisory database on every restore, and `-warnaserror` promotes a hit to an
error.

```
error NU1904: Package 'X' 1.2.3 has a known critical severity vulnerability
```

So a newly disclosed advisory can turn a build red with no commit behind it. That
is the intent — this app handles OAuth credentials and patient data.

Two rules are overridden in `.editorconfig`, each with its reason written beside
it: `IDE0005` is raised to a warning, because an unused `using` is not a matter of
taste; and `CA1707` is switched off under `tests/`, where method names are
sentences rather than identifiers.

## The pinned toolchain

`global.json` pins the SDK to 10.0.400 and rolls forward to any later 10.0.x. A
`packages.lock.json` beside each project pins the resolved dependency graph. Between
them, a build here, on your machine, and in CI sees the same compiler, the same
analyzers and the same packages.

Adding or upgrading a package rewrites the lock file during `dotnet restore` —
commit it with the change. CI restores with `--locked-mode`, which fails rather
than quietly resolving something new.

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
  redirect, held in `IMemoryCache` under the OAuth `state` for five minutes — that,
  and the signing keys an EHR publishes, which are cached for an hour under their
  own URL because they are public, identical for every launch, and rotate on the
  order of months. The
  access token is used once and discarded, so `/callback` renders in the same
  request that exchanges the code — and so does `/learn`'s exchange, which is why
  its later steps read a transcript rather than resume a live launch. That
  transcript is the one thing the narrated launch adds to the cache: no credential,
  but patient data, so it expires on the same five minutes and every `/learn` page
  sends `Cache-Control: no-store`.
- **The id_token is validated, though it need not be.** OIDC Core 3.1.3.7 lets an
  app skip signature validation when the token arrives over a direct TLS connection
  to the token endpoint, which is exactly how it arrives here. This app checks the
  signature against the EHR's published keys anyway, along with `iss`, `aud` and
  expiry, because the keys are one cached fetch away and an app that only checks
  when it must is one deployment change away from not checking when it should. The
  rules live in `IdToken` as a pure function of the token, the keys and the clock;
  fetching and caching the keys is `Jwks`, kept separate.
- **The launching user is projected off the base resource.** `fhirUser` may name a
  Practitioner, Patient, RelatedPerson or Person, so `LaunchUser` selects `name` and
  `identifier` with FHIRPath against `Resource` — which handles all four in less code
  than handling one would take alone. It keeps a name's `prefix`, because "Dr.
  Albertine Orn" is most of how a clinician is addressed.
- **A fhirUser pointing elsewhere is not followed.** The reference is read relative to
  the launch's own FHIR server, or absolute if it names that same origin. An absolute
  reference to another origin is refused, because following it would send this
  launch's access token to a server the token was never issued for.
- **Identity degrades, it does not fail.** No `openid` grant, no published
  `jwks_uri`, unreachable keys, or a token that fails validation each leave the
  launch standing with a sentence saying why nobody is named. The app's job is the
  patient summary, and it should not be lost to an absent name.
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

The app has two direct packages: `Hl7.Fhir.R4` 6.4.0 (BSD-3-Clause) for the FHIR
work, and `Microsoft.IdentityModel.JsonWebTokens` 8.22.0 (MIT) to validate the
id_token. Everything else is in-box ASP.NET Core.

The second is there rather than hand-rolled deliberately: verifying a JWS against
a published JWKS is exactly the kind of code that is easy to write and easy to
write wrongly, and getting it wrong is silent.

The tests add `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing` and
`Microsoft.Testing.Extensions.CodeCoverage`, and nothing else — no mocking library,
no container library. `global.json` opts `dotnet test` into
Microsoft.Testing.Platform, which the .NET 10 SDK requires, and pins the SDK; see
Analyzers.

Two dev-time tools are pinned in `.config/dotnet-tools.json`: `CSharpier` 1.3.0
(MIT), which formats the source, and `dotnet-coverage` 18.10.0 (MIT), which merges
the two coverage reports into one. Neither ships in anything.

## License

MIT — see [LICENSE](LICENSE).
