# Plan: testing scaffolding and two pattern fixes

Status: in progress on branch `tests-and-issuer-allowlist`. Steps are executed one
at a time, each ending in a verifiable state.

- [x] Step 1 — Restructure
- [x] Step 2 — Unit tests for the already-pure code (+ birth-date precision fix)
- [x] Step 3 — Integration test against the real launcher
- [ ] Step 4 — Extract the core, thin the adapters
- [ ] Step 5 — Trusted-issuer allowlist
- [ ] Step 6 — README

## Why

The demo explains a SMART EHR launch well, but it has no tests, and two of its
patterns are the kind a reader would copy into real code. This plan closes those
two, leaves the rest alone deliberately, and builds enough test scaffolding to
prove the launch works against a real SMART server rather than a stub that only
agrees with us.

## Scope

**In scope**

1. Trusted-issuer allowlist for `iss`.
2. Decoupling the SMART protocol from ASP.NET types, so the launch is testable
   without a web host and `/launch` and `OnGetAsync` become thin adapters.
3. A unit test project and an integration test project.

**Deliberately out of scope** — flagged during the dig, left as-is for now:

- `Summary { get; private set; } = default!` (`Callback.cshtml.cs:24`) — safe only
  because `Fail()` returns before `Page()`; the construction is invisible.
- Raw token-endpoint response bodies pushed into `/error?message=…`
  (`Callback.cshtml.cs:56`) — lands in history, proxy logs, referrers.
- `IMemoryCache` as the launch store — single replica only.
- Unnamed `clients.CreateClient()` mutated per call.
- Raw Patient JSON rendered on the page.
- CI. See "Prep for CI" below.

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| `iss` matching | Origin match (scheme + host + port) | The launcher encodes launch params in the iss *path* (`/v/r4/sim/{base64}/fhir`), so exact-URL matching cannot work |
| Default allowlist | Pre-trust `https://launch.smarthealthit.org` | Keeps the README's out-of-box path working; the allowlist stays visible in config |
| Core failure reporting | Closed `record` hierarchy + `switch` expression | Exhaustive, immutable, no dependency; keeps error *kinds* instead of flattening to strings |
| Layout | `src/` + `tests/` + `.sln` at root | Conventional with three projects; costs the clone-and-run story (see step 1) |
| Integration transport | `WebApplicationFactory` + host-routing `DelegatingHandler` | One `HttpClient` with `AllowAutoRedirect` walks the whole chain; only one production change needed |
| Containers | Launcher only, started outside the tests | The launcher never calls back into the app, so containerising the app buys nothing. Testcontainers was dropped — see below |
| Hermeticity | Not hermetic; needs internet + Docker | Accepted. Escape hatch is one env var — see "Prep for CI" |

## What the dig established

Verified by running the flow against the public launcher, not assumed:

- **No browser automation needed.** With `skip_login` + `skip_auth` in the launch
  params, `/authorize` returns 302 straight to `redirect_uri` with a code — zero
  intermediate hops. The integration test is plain `HttpClient`.
- Full chain confirmed: discovery -> PKCE S256 -> token -> `/metadata`
  (`fhirVersion: 4.0.0`, so `VerifyFhirVersion = true` passes) -> `Patient/{id}`.
- **The launcher simulates real failures.** `auth_error` values
  `auth_invalid_client_id` / `auth_invalid_scope` / `auth_invalid_redirect_uri`
  arrive as `error=` on the callback (hits `Callback.cshtml.cs:34`);
  `token_invalid_token` returns a genuine 401 (hits `:55`).
- Therefore faked-HTTP unit tests only need to cover what the launcher *cannot*
  produce: allowlist rejection, unknown/expired `state`, a token response with no
  `patient`, and `PatientSummary.From` branches.
- `smart-dev-sandbox` is archived (Jul 2025) — reference only, not a foundation.
- `smartonfhir/smart-launcher-2` publishes **only** `latest` (pushed 2025-02-13,
  209MB, linux/amd64+arm64). Pin by digest:
  `sha256:72bd3e3c682ce4c74e6dddb605d89acad7c8aae446ae38079e8dfe8455b84793`

## New dependencies

| Package | Version | Where |
| --- | --- | --- |
| `xunit.v3` | 4.0.0 | both test projects |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.11 | integration tests |

No mocking library. HTTP is faked with a stub `HttpMessageHandler`.

**Tooling notes found while building this:**

- The .NET 10 SDK dropped VSTest support from `dotnet test`. Opting in to
  Microsoft.Testing.Platform is a `test` section in **`global.json`** — not
  `dotnet.config`, and not the `TestingPlatformDotnetTestSupport` property, which is
  the older VSTest-bridge route. Consequence: `Microsoft.NET.Test.Sdk` and
  `xunit.runner.visualstudio` are *not* referenced; they are the VSTest path and
  their presence is what breaks the build.
- Test projects need `<OutputType>Exe</OutputType>` (xunit v3 apps self-host) and a
  `FrameworkReference` to `Microsoft.AspNetCore.App`.
- `dotnet new sln` on .NET 10 emits `.slnx`, not `.sln`. Fine for the CLI and
  current IDEs; worth knowing if anything older ever needs to open it.
- Firely rejects a resource with no elements ("Empty FHIR elements are invalid"),
  so the minimal valid fixture is `{"resourceType":"Patient","id":"..."}`.

---

## Steps

The order puts the integration test *before* the refactor, so the risky structural
change happens with an end-to-end net already under it.

### Step 1 — Restructure

Move the app to `src/SmartOnFhirDemo/`, create `tests/`, add a solution at root.
No behaviour change.

- Update `.gitignore` for the new paths.
- README: `dotnet run` becomes `dotnet run --project src/SmartOnFhirDemo`.

**Done when:** `dotnet build` succeeds and a manual launch from
launch.smarthealthit.org still renders a patient.

### Step 2 — Unit tests for the code that is already pure

`PatientSummary.From` and `Smart` need no refactor to be testable. Test them first;
this proves the harness before anything structural moves.

- `PatientSummary.From`: `use='official'` name preference and fallback, partial
  birth dates (`1974`, `1974-12`) and age calculation, MRN identifier selection,
  home-address preference, `maritalStatus.text` vs `coding.display` fallback,
  absent fields rendering `—`, and `Fields` ordering.
- `Smart`: PKCE verifier/challenge are S256-correct and URL-safe, `NewState` is
  unpredictable, `BuildAuthorizeUrl` emits every required parameter and encodes
  correctly.

**Bug found and fixed here (agreed in-flight, was not in the original scope).**
`FormatBirthDate` claimed to fall back to the raw value for partial dates, but
`Date.TryToDateTimeOffset` *succeeds* on them by defaulting to January 1st — so the
fallback was unreachable and `birthDate: "1974"` rendered as `1974-01-01 (52 yrs)`,
inventing both a day and an age. Precision now comes from
`TryToSystemDate(...).Precision`, and a partial date renders as written with no age.

Age is asserted as shape, not a fixed number: `FormatBirthDate` reads
`DateTimeOffset.UtcNow` directly, so any specific age would rot. The birthday-boundary
arithmetic is therefore still uncovered — injecting a `TimeProvider` was considered
and deliberately declined.

Patient fixtures are checked-in JSON, parsed with Firely — deterministic, and
independent of whatever the public sandbox happens to be serving.

**Done when:** `dotnet test tests/SmartOnFhirDemo.UnitTests` is green and offline.

### Step 3 — Integration test against the real launcher

The safety net for step 4. Written against current behaviour.

- Add `public partial class Program { }` to `Program.cs` — the only production
  change this step needs (top-level statements otherwise produce an internal
  `Program`, which `WebApplicationFactory<Program>` cannot reach).
- The launcher is expected to be already running, at `SMART_LAUNCHER_URL`. Tests
  that need it skip when it is unset, so `dotnet test` stays green without it.
- A `DelegatingHandler` routes requests for the app's host into the `TestServer`
  and everything else to the real network, so one `HttpClient` with
  `AllowAutoRedirect = true` walks launch -> authorize -> callback in a single GET.
- Encode launch params with `skip_login` + `skip_auth`, `pkce: always`,
  `launch_type: provider-ehr`.

Tests:
- Happy path: a launch renders a summary. **Assert on shape, not values** — the
  sandbox can be reseeded, so assert fields are present and populated, never that
  the patient is named Koepp.
- Error paths driven by the launcher's `auth_error`: `auth_invalid_client_id`,
  `auth_invalid_scope`, `auth_invalid_redirect_uri`, `token_invalid_token`.
- `/callback` with an unknown `state` renders the expired-launch error.
- `/launch` with missing `iss` or `launch` renders the parameter error.

**Done when:** the suite is green against the public launcher.

**Testcontainers was dropped here.** The plan assumed the tests could start the
container themselves, which needs a user-reachable Docker socket. Omarchy
deliberately keeps users out of the `docker` group — its own install script calls
membership "equivalent to passwordless root", and migration `1787580187.sh`
actively removes existing users from it. The sanctioned opt-in is
`omarchy setup security sudoless docker` (plus a reboot); Arch also ships no
`docker-rootless-extras`, so rootless is hand-rolled. Rather than weaken the host's
security posture to run a test suite, the launcher is started outside the tests and
located through an environment variable. This also removed a dependency.

The tests split by what they need:

- `AppOnlyTests` — the failures the app reaches on its own (missing parameters,
  unknown callback state, an unreachable issuer). No Docker, always run.
- `SmartLaunchTests` — everything needing a real EHR. Skips with an actionable
  message when `SMART_LAUNCHER_URL` is unset.

### Step 4 — Extract the core, thin the adapters

No behaviour change intended; step 3 must stay green throughout.

A `SmartLaunch` type owns the protocol and its HTTP. It does **not** touch
`IMemoryCache` — caching is a hosting concern and stays in the shell, which keeps
the core a function of its inputs.

- `BeginAsync` -> `LaunchOutcome`: `Prepared(AuthorizeUrl, State, LaunchState)`,
  `MissingParameters`, `DiscoveryFailed(Iss, Reason)`.
  (`UntrustedIssuer` is added in step 5.)
- `CompleteAsync` -> `CallbackOutcome`: `Completed(Summary, RawJson)`,
  `AuthorizationDenied(Reason)`, `MissingParameters`,
  `TokenExchangeFailed(Status, Reason)`, `NoPatientContext`,
  `PatientReadFailed(Reason)`, `IncompatibleFhirVersion(Reason)`.

`/launch` and `OnGetAsync` reduce to: read parameters, look up or store the cache
entry, call the core, `switch` the outcome onto a result. The cache lookup miss
stays in the adapter as the expired-launch case.

Add unit tests for the outcomes the launcher cannot produce — no `patient` in the
token response, discovery returning an empty body — using a stub
`HttpMessageHandler`.

**Done when:** integration tests still green, adapters are a `switch`, and the
core has unit tests.

### Step 5 — Trusted-issuer allowlist

The behaviour change. Closes an SSRF (the app fetches any host's `.well-known`),
an open redirect (it forwards the user to whatever `authorization_endpoint` that
document names), and a credential leak (it POSTs the auth code and `client_id` to
whatever `token_endpoint` it names).

- `Smart.IsTrustedIssuer(iss, trusted)` — pure, origin-compared, unit-tested. It is
  the security-critical function, so it gets adversarial tests:
  `https://launch.smarthealthit.org@evil.example` (userinfo — `Uri.Host` is
  `evil.example`), host casing, trailing dot, explicit vs default ports, non-HTTP
  schemes, and malformed or relative input.
- `SmartOptions.TrustedIssuers`, defaulting to `["https://launch.smarthealthit.org"]`
  in `appsettings.json`.
- Add `LaunchOutcome.UntrustedIssuer(Iss)`; check it **before** discovery so an
  untrusted host is never contacted at all.
- The integration fixture overrides config with the container's
  `http://localhost:{mappedPort}` origin.
- Integration test: an untrusted `iss` is rejected without an outbound request.

**Done when:** unit and integration suites are green and an untrusted `iss` is
refused.

### Step 6 — README

Document the allowlist and how to add an issuer, the new run command, and how to
run each test suite (including that integration tests need Docker and internet).

---

## Prep for CI — not now

Captured so the later decision is cheap. Nothing here is built yet.

**What CI would depend on**

- *Docker Hub pull of the launcher* — a non-issue on GitHub-hosted runners, which
  are exempt from pull limits for public images. Self-hosted runners are **not**
  exempt (10 anonymous pulls/hour, shared per IP); fix by authenticating or
  mirroring to GHCR.
- *`r4.smarthealthit.org`* — the real risk. Its headers report
  `Smile CDR 2019.08.PRE … HAPI FHIR 4.0.0-SNAPSHOT`: a 2019 pre-release,
  untouched for ~7 years. Fast today (~180ms), but no SLA, no status page to gate
  on, and no rate-limit headers — you would find the limits by collecting 429s.

**Failure modes:** third-party downtime reddening CI for unrelated reasons; data
drift breaking value assertions; unknown rate limits under matrix builds.

Only data drift constrains what we build now, and step 3 already handles it by
asserting on shape rather than values.

**Escape hatch.** The launcher takes its upstream from a single env var,
`FHIR_SERVER_R4`. Going hermetic later touches the fixture only — no test bodies,
no production code. Two options:

- `hapiproject/hapi` (actively maintained, 436MB) plus a seeding step. Realistic,
  but 30–90s boot per run.
- A stub FHIR server in the test process. Faster, but it must serve a valid R4
  `CapabilityStatement` at `/metadata` (because `VerifyFhirVersion = true` fetches
  it first) plus `/Patient/{id}`, and the launcher container has to reach back to
  the host — `WithExtraHost("host.docker.internal", "host-gateway")` on Linux.

**Recommended shape when we get there.** The two-project split already does the
work: unit tests on every push (fast, offline, gating PRs); integration tests on a
nightly schedule plus `workflow_dispatch`, not on PRs. A red nightly is then real
signal — either the code broke or the sandbox changed — without landing as noise
on someone's pull request.
