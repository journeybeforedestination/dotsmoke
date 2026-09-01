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
  app  file the launch against the browser's session cookie
  app ──302──▶ /summary?id={launchId}&patient={id}   ──▶ the summary
  reader ──GET /summary?…&show=conditions──▶ app
  app ──GET {iss}/Condition?patient={id}  Bearer──▶ Bundle  ──▶ the panel
```

`/callback` renders nothing. It exchanges, files what it got against the browser, and
sends the reader to a URL that names the launch — which takes the authorization code
out of the address bar, and stops a refresh from re-sending a code already spent. The
patient rides along in that URL so every page says which one it believes it is showing.

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

The app creates `app.db` beside itself on first run and migrates it on every start.
That is the access log, and deleting the file loses nothing but the log; point
`ConnectionStrings:AccessLog` somewhere else to put it elsewhere.

Beside it goes `keys/`, the data protection key ring, which is what signs the antiforgery
token on `/learn`'s exchange form. It is kept rather than minted afresh at every boot
because a reader paused on step ④ across a restart would otherwise fail at the exchange,
with an error naming nothing useful. `DataProtection:KeyRing` moves it, and a deployment
points it and the log at one volume. Nothing encrypts the keys at rest — every option for
that means holding a second key somewhere — so the app logs a warning saying so on every
start, and the directory's permissions are what protect them.

`GET /up` answers 200 and nothing else. It is what a proxy asks before it sends a reader
here, and `/up` is kamal-proxy's default path, so matching it means a deployment
configures no health check at all. It stays shallow deliberately: the app migrates its
database before it serves, so a missing or unwritable volume has already stopped the
process, and a check that reopened the database every second would re-prove that forever.

`Smart:PublicOrigin` is the address readers reach this app on, and it is required: the
app is told its origin rather than reading one off the incoming request, so every URL it
hands an EHR is one a browser can come back to. It ships as `http://localhost:5000`,
matching the launch profile; behind a proxy that terminates TLS it is the public
`https://` origin, and nothing about the app changes. A missing or malformed one refuses
to start.

To launch from a different EHR, add its issuer to `Smart:TrustedIssuers` in
`appsettings.json` — the app refuses launches from anywhere not on that list, and refuses
to start if the list is empty:

```json
"Smart": {
  "TrustedIssuers": [ "https://launch.smarthealthit.org", "https://ehr.example" ]
}
```

`Smart:Scopes` is what the app asks each of them for. `openid fhirUser` is what makes
an EHR say who started the launch, and `user/Practitioner.read` is what lets that
person's name be read; drop either and the launch still works, and the summary simply
says nobody was named. The three `patient/` scopes after them are the summary's panels,
and dropping one of those degrades the same way: the panel says the EHR would not answer
rather than disappearing. They are v1 syntax — `patient/Condition.read`, not
`patient/Condition.rs`.

## The summary, and reading on from it

The launch lands on `/summary?id={launchId}&patient={id}`, and that URL keeps working
until the EHR's token runs out. Three panels read on from it — conditions, vital signs
and medications — each a link rather than any JavaScript, because a link is enough and
this app has no script at all.

Those reads are searches, not reads by id: `Condition?patient={id}` rather than
`Condition/{id}`. That is the shape a `patient/Condition.read` scope actually
authorises — a class of data about one patient, not one URL — and it is the first place
this app hands Firely a `Bundle` to unpack rather than a single resource. Every panel
degrades to a sentence: a patient with nothing recorded says so, and a scope the EHR
declined to grant says that instead, because an empty list and a refusal look identical
on screen and mean opposite things.

Both round trips are written to the access log, which is the point of capturing at the
transport: the panels were added after the handler, and did not have to remember to
audit themselves.

`/learn` ends on the same panels, from the same shared partial, reading through the same
service. That is deliberate rather than incidental — a walkthrough that left you with
less than the plain launch would be teaching a smaller app than the one it narrated —
and an integration test asserts it, because parity is exactly the kind of claim that
rots quietly.

Worth knowing before you read too much into a green run: the SMART App Launcher does
not enforce scopes the way a real EHR does. A launch against it shows the searches
working; it shows rather less about what happens when a scope is refused.

## The same launch, narrated

`/learn` is the second launch URL. It runs the identical protocol against the identical
EHR — same trust check, same discovery, same PKCE, same token exchange — and now opens
the identical session, so it ends on the same summary with the same panels. It differs
only in stopping where the plain launch redirects, to explain what was exchanged before
going on. The narration is of this app, not of a simpler one.

```
SMART Launcher ──GET /learn?iss=…&launch=…──▶ app
  app  discovery, PKCE, authorize URL          ──▶ ① what the EHR sent
                                                   ② what discovery returned
                                                   ③ what is about to be sent   [continue]
  launcher (provider login → patient/consent) ──302──▶ app /learn/callback?code=…&state=…
  app                                          ──▶ ④ the code, still unspent    [exchange]
  app ──POST {token}──▶ the launch is filed against the browser's session
  app  validate id_token; ──GET {iss}/Patient/{id} and {iss}/{fhirUser}  Bearer
  app  ──302──▶ /learn/token?id=…&patient=…
                 ──▶ ⑤ what the token endpoint returned                        [continue]
                     ⑥ the session it opened, and what names it
               /learn/user    ──▶ ⑦ who launched it, and how that was proved   [continue]
               /learn/patient ──▶ ⑧ the FHIR read, the summary, and the panels
```

Step ⑧ ends where `/summary` does, panels included. The reads behind those buttons
happen when you press them, not during the handshake, which is the point the last page
makes: the launch is still open, and that is what step ⑥ bought.

Every page carries the eight steps across the top, so you can see where you are
and how much is left.

Notice where the URL changes. Up to the exchange the walkthrough is keyed by the OAuth
`state`, because there is no launch yet — only one in flight. After it, by the launch id
and the patient, because now there is one. Step ⑥ is about that swap: the same session
the plain launch opens, narrated, including why a cookie alone would be the wrong shape
for it.

## What SMART demands

The four things the protocol makes an app responsible for, and what this one does
about each. `/learn` shows all four happening.

- **The issuer is checked against an allowlist.** `iss` arrives as a query
  parameter and everything downstream trusts it: the app fetches that host's
  configuration, sends the user to the authorization endpoint it names, and posts
  the authorization code to its token endpoint. Unchecked, that is a server-side
  request forgery, an open redirect, and a way to harvest codes. `TrustedIssuers`
  lists the EHRs a launch may come from, compared by origin because a SMART issuer
  legitimately carries a path. An empty list trusts nobody.
- **The id_token is validated, though it need not be.** OIDC Core 3.1.3.7 lets an
  app skip signature validation when the token arrives over a direct TLS connection
  to the token endpoint, which is exactly how it arrives here. This app checks the
  signature against the EHR's published keys anyway, along with `iss`, `aud` and
  expiry, because the keys are one cached fetch away and an app that only checks
  when it must is one deployment change away from not checking when it should. The
  rules live in `IdToken` as a pure function of the token, the keys and the clock;
  fetching and caching the keys is `Jwks`, kept separate.
- **A fhirUser pointing elsewhere is not followed.** The reference is read relative to
  the launch's own FHIR server, or absolute if it names that same origin. An absolute
  reference to another origin is refused, because following it would send this
  launch's access token to a server the token was never issued for.
- **Identity degrades, it does not fail.** No `openid` grant, no published
  `jwks_uri`, unreachable keys, or a token that fails validation each leave the
  launch standing with a sentence saying why nobody is named. The app's job is the
  patient summary, and it should not be lost to an absent name.

## How this code is arranged

Design decisions about the .NET side rather than about SMART.

- **The protocol is separate from the web layer.** `SmartLaunch` does discovery,
  the authorization request, the token exchange and the patient read, returning a
  closed set of outcomes; the `/launch` and `/callback` endpoints map those onto
  responses.
  That separation is what lets the launch be tested without a web host.
- **The OAuth handshake is hand-rolled** over `HttpClient`. SMART reveals the
  issuer only at launch time, which ASP.NET's `OpenIdConnect` middleware — built
  around a static `Authority` — fights.
- **The log goes to disk; the credential stays in memory.** The access log is the
  app's own data rather than the EHR's, and the only thing a launch produces that
  outlives the process: a SQLite file, one table, created by an EF Core migration at
  start-up. Nothing a launch carries joins it. The data protection key ring sits beside
  it and outlives a restart too, but it holds no launch data either — only the keys that
  sign `/learn`'s antiforgery token, which is why losing them across a deploy would break
  a walkthrough mid-exchange. Rows are written by a `DelegatingHandler`
  wrapped around every FHIR request rather than by the pages, because a read added
  later cannot forget to audit itself — there is one place requests leave. That
  handler is constructed per launch and handed its context, never given one to
  resolve: `IHttpClientFactory` pools handlers across requests, and one holding a
  "current launch" would file one patient's read under another's. The issuer and PKCE verifier survive the
  redirect in `IMemoryCache` under the OAuth `state` for five minutes — that, and
  the signing keys an EHR publishes, which are cached for an hour under their own
  URL because they are public, identical for every launch, and rotate on the order
  of months. The
  access token now outlives the request that fetched it, filed against the browser's
  session cookie and expiring when the EHR said it stops working — that is what a
  summary you can return to costs, and it is why the cookie is `HttpOnly` and the
  launch id is 256 bits of CSPRNG. `/learn` opens the same session on the same terms —
  it narrates this app, so it cannot hold less than this app does — and both it and
  `/summary` send `Cache-Control: no-store`, because every one of those pages is
  patient data. There is no second mechanism for the walkthrough: the account it
  renders is the credential-free half of the launch the summary resolves.
- **The launching user is projected off the base resource.** `fhirUser` may name a
  Practitioner, Patient, RelatedPerson or Person, so `LaunchUser` selects `name` and
  `identifier` with FHIRPath against `Resource` — which handles all four in less code
  than handling one would take alone. It keeps a name's `prefix`, because "Dr.
  Albertine Orn" is most of how a clinician is addressed.
- **Two launches in one browser stay apart.** A cookie is per browser; a launch is per
  patient. So the session is a map from launch id to context rather than a single slot:
  a second tab launching a second patient adds a launch instead of replacing one. The
  cookie is set where a launch completes rather than where one starts — a session is
  what holds a launch, so there is no reason for one to exist before there is a launch
  to put in it, and a browser reaching the callback without one has simply not launched
  before. That is also what makes `SameSite=Lax` load-bearing: the EHR's redirect back
  is a cross-site top-level GET, and under `Strict` a second launch would arrive without
  the first's cookie and open a session of its own, putting the first out of reach. Get
  that wrong and the failure has one specific shape — the first tab, still showing
  patient 123's banner, asks for more and is handed patient 456's data, silently, with
  no error anywhere. On top of that, every page carries the patient it believes it is
  showing and is refused if the launch disagrees. An expired launch and a mismatched one
  land on the same re-launch prompt, because there is nothing a reader can do
  differently about either; the access log keeps them apart, because one is time passing
  and the other is a safety violation.
- **A credential never reaches a page model.** A summary you can come back to needs
  the access token to outlive the request that fetched it, so the old rule — that
  credentials are removed where they arrive — cannot hold as written. What replaces it
  is a line drawn one level lower. `LaunchContext` names the token, and only the cache
  and the code that makes FHIR requests ever see one; `LaunchFacts` is the same launch
  with the token taken off, and it is what a page resolves to. `SmartLaunch` still
  redacts the token response into `TokenFacts` before returning, and the token is on no
  outcome at all — the context it establishes is a separate return, so the account the
  walkthrough renders cannot acquire one by accident. `LaunchTranscript` is handed
  `LaunchFacts`, which is why step ⑥ can describe the session without being able to
  show the cookie or the token. A page cannot leak what it was never handed, which
  beats a page that remembers not to print it.
- **The explanation is a pure projection.** `LaunchTranscript` turns outcomes into
  ordered steps with their fields, payloads and prose. It does no I/O and reaches
  nothing but what it is given, so the narrated launch is readable and reviewable in
  one file, and the pages stay markup.
- **Firely does the FHIR work**, not just the HTTP call: FHIRPath for element
  selection, `EnumUtility` for coded display text, `Date` for partial birth
  dates, `OperationOutcome` for server errors, and `Bundle` plus the typed POCOs
  for the summary's panels — one `CodeableConcept` shape names a condition, an
  observation and a medication alike.

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
dotnet ef migrations has-pending-model-changes \
  --project src/SmartOnFhirDemo --no-build   # the schema still matches the model
dotnet test --no-build --coverage --coverage-settings coverage.config \
  --coverage-output-format cobertura
./.github/coverage.sh                    # merge the two reports, report the rate
```

No database is provisioned for any of that, which is most of why SQLite is the
choice: it is a library in the process rather than a server, so there is no
container to start and no connection string to hold. The unit tests migrate an
in-memory database — from the committed migration rather than `EnsureCreated`, so
what they run against is what would ship — and the integration tests boot the real
app against a temporary file they delete afterwards. Nothing survives the job, so
every run builds the schema from scratch.

The migration check is there because the failure it catches is silent. Editing
`AccessLogEntry` or `OnModelCreating` without running `dotnet ef migrations add`
leaves a schema that is missing the change; `Database.Migrate()` still starts
cleanly against it, and the first query is where it goes wrong.

That last line runs both projects, not only the fast one. Twenty-seven of the forty-six
integration tests need no launcher — among them every untrusted-issuer refusal, which is
the app's central security property — and the nineteen that do skip themselves. The whole
job stays offline.

The launcher-bound tests run in a second job, nightly and on demand, which starts the
container first. Because it runs the whole suite, that job is also where the coverage
floor lives — currently 93%, against a measured 97.2%. The gap is slack, not laxity:
the job reaches a public sandbox that can be reseeded, and a patient turning up without
a phone number should not read as a regression. Because those tests skip themselves when `SMART_LAUNCHER_URL`
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

[CSharpier][csharpier] owns the layout of every `.cs`, `.csproj`, `.props` and
`.config` file here — the last of those being `coverage.config`, which it picked up
by itself and reindented, so it may as well keep it. It is pinned in `.config/dotnet-tools.json`, so a clone gets the same version:

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

Rules are overridden in `.editorconfig` in three places, each with its reason
written beside it: `IDE0005` is raised to a warning, because an unused `using` is
not a matter of taste; `CA1707` is switched off under `tests/`, where method names
are sentences rather than identifiers; and both `IDE0005` and `CA1861` are switched
off under `src/**/Migrations/`, which `dotnet ef` writes and rewrites — satisfying
them there would mean hand-editing a generated file, only for the next migration to
undo the edit.

## The pinned toolchain

`global.json` pins the SDK to 10.0.400 and rolls forward to any later 10.0.x. A
`packages.lock.json` beside each project pins the resolved dependency graph. Between
them, a build here, on your machine, and in CI sees the same compiler, the same
analyzers and the same packages.

Adding or upgrading a package rewrites the lock file during `dotnet restore` —
commit it with the change. CI restores with `--locked-mode`, which fails rather
than quietly resolving something new.

## Dependencies

The app has three direct packages: `Hl7.Fhir.R4` 6.4.0 (BSD-3-Clause) for the FHIR
work, `Microsoft.IdentityModel.JsonWebTokens` 8.22.0 (MIT) to validate the id_token,
and `Microsoft.EntityFrameworkCore.Sqlite` 10.0.11 (MIT) for the access log.
Everything else is in-box ASP.NET Core.

The second is there rather than hand-rolled deliberately: verifying a JWS against
a published JWKS is exactly the kind of code that is easy to write and easy to
write wrongly, and getting it wrong is silent.

The third is EF Core rather than raw ADO.NET over `Microsoft.Data.Sqlite`, which
would have been one package fewer and would have kept the SQL on the page. The
migrations are what tipped it: a schema that changes needs versioning, and
hand-rolling that is the wrong kind of small.
`Microsoft.EntityFrameworkCore.Design` 10.0.11 (MIT) comes with it, marked
`PrivateAssets` because it is only what `dotnet ef` builds a model against.

The tests add `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing` and
`Microsoft.Testing.Extensions.CodeCoverage`, and nothing else — no mocking library,
no container library. `global.json` opts `dotnet test` into
Microsoft.Testing.Platform, which the .NET 10 SDK requires, and pins the SDK; see
Analyzers.

Three dev-time tools are pinned in `.config/dotnet-tools.json`: `CSharpier` 1.3.0
(MIT), which formats the source, `dotnet-coverage` 18.10.0 (MIT), which merges the
two coverage reports into one, and `dotnet-ef` 10.0.11 (MIT), which writes the
migrations and, in CI, checks they still match the model. None of them ships in
anything.

## License

MIT — see [LICENSE](LICENSE).
