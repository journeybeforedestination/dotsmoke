# Ideas

Things considered and set aside, kept short so they can be picked up later.
Roughly ordered by value within each half.

Written alongside the SMART SSO work (`fhirUser` validation and the coverage
gate), which is done — these are what was deliberately left out of it.

## Teaching SMART

- **Standalone launch.** The other half of SMART: no `launch` parameter, the app
  initiates, asks for `launch/patient`, and the EHR shows a patient picker. It is
  the flow patient-facing apps use, and the app teaches neither it nor its
  existence. `SmartLaunch.BeginAsync` requires `launch` to be non-empty, which is
  the one structural blocker — lifting it would make the method parameterised
  over launch type, a real abstraction rather than a bolted-on feature. The
  launcher supports it (`launch-standalone`). Biggest remaining conceptual gap.

- **SMART v2 granular scopes.** Move from `patient/Condition.read` to v2 syntax
  (`patient/Condition.rs`), and deliberately ask for more than will be granted so
  the requested-versus-granted gap is visible on screen. That gap is the whole
  lesson, and it is the half of this idea the summary's panels did not do: they
  brought the second resource, FHIR search and Bundle handling, but they ask in
  v1 and show nothing about what was actually granted. Launcher supports
  `permission-v1` and `permission-v2` — but note what its `scope` simulation
  actually does, which a test now pins: asked to grant less than the app
  requested, it refuses the launch at the authorization step and names the scopes
  it withheld, rather than returning a narrowed token. So the gap cannot be shown
  on a summary reached through it, and this idea needs either a different EHR or
  a request the launcher will part-grant.

- **Discovery as a negotiation.** The app reads four fields out of
  `.well-known/smart-configuration` — the two endpoints, plus `issuer` and
  `jwks_uri` for id_token validation — and ignores the rest. Read `capabilities` and
  `code_challenge_methods_supported`, refuse an EHR that cannot do S256, and
  narrate the trust boundary at step 2: `authorization_endpoint` and
  `token_endpoint` come out of the issuer's own document and are used
  unconditionally. That is *correct* — SMART explicitly allows auth endpoints on
  a different host than the FHIR base — but it is the sharpest trust point in the
  flow and the narration is silent on it. Small, no dependencies.

- **Refuse a bad id_token.** The app degrades on every identity failure: a forged
  signature, a wrong audience and a missing `openid` grant all leave the launch
  standing with a sentence saying why nobody is named. A forged token is different
  in kind from an absent one, and a hard `IdTokenInvalid` outcome would match how
  `UntrustedIssuer` already behaves. The launcher cannot easily be made to emit a
  bad id_token, so the failure path would be unit-tested only.

- **Refresh tokens and `offline_access`.** The access log took "nothing is
  persisted" off the table, but the property that replaced it — the log goes to
  disk, the credential stays in memory — is the one a refresh token breaks, and
  it is the sharper of the two. Needs an app whose users come back, so the access
  token expiring is still handled as a re-launch prompt. Could be taught as
  explanation only: a step saying what `offline_access` would change, and why
  this app does not ask for it.

- **Confidential clients — `private_key_jwt`.** Asymmetric client authentication,
  and from there SMART Backend Services (client credentials, no user). A whole
  second world, arguably past "basic SMART launch". Launcher supports
  `client-confidential-asymmetric`, and its sim takes `jwks_url` / `jwks`.

- **Token introspection.** The launcher publishes an `introspection_endpoint`.
  Niche, but it is the answer to "how does a resource server check this token".

## Demonstrating .NET

- **Fail-fast configuration.** `AddOptions<SmartOptions>().Bind(…).Validate(…)
  .ValidateOnStart()`. Today a typo'd or empty `TrustedIssuers` starts cleanly
  and fails at launch time with a message about app registration, which sends you
  looking in the wrong place. Security config that refuses to boot is the
  technique, and this app is close to the ideal case for it. No dependencies,
  small.

- **Typed clients and resilience.** The FHIR reads now go through a named client,
  because the access log has to wrap its handler — but naming it was all that
  bought. It and the other three calls — discovery, the token exchange, the JWKS
  fetch — still take the default 100-second timeout, no retry, and no user-agent
  identifying the app to the EHR. Real timeouts on each, plus
  `Microsoft.Extensions.Http.Resilience`. Directly relevant given the nightly job
  depends on a 2019-vintage public sandbox with no SLA.

- **A clock `PatientSummary` can be given.** `FormatBirthDate` still reads
  `DateTimeOffset.UtcNow` directly, which is why its test asserts shape rather than
  a value — "any specific value would rot". The seam already exists elsewhere:
  `TimeProvider` is registered and injected into `SmartLaunch` so the id_token's
  lifetime can be checked against a fixed clock. Either extend that to the
  projection or, closer to the functional-core preference, pass the day in.

- **The `prefix` bug, and one name formatter.** There are now two name formatters
  that disagree: `LaunchUser.Format` keeps a name's `prefix` because "Dr. Albertine
  Orn" is most of how a clinician is addressed, and `PatientSummary.Format` drops
  it. That is a real latent bug for any patient with a prefix. Extract one
  formatter, fix the prefix, delete the duplication — deliberately deferred so the
  SSO diff stayed narrower than the feature.

- **Container publish with no Dockerfile.** `dotnet publish /t:PublishContainer`
  is in-box in the SDK and many people do not know it exists. Caveat: loading the
  built image locally needs a Docker socket, which this machine only has under
  sudo.

- **Mutation testing.** Stryker.NET against a suite this carefully written would
  either validate it or be very interesting. Third-party and slow, so probably a
  one-off measurement worth writing up rather than a CI gate.

- **Supply-chain provenance.** SBOM generation and build attestation in CI, both
  GitHub-native. Fits a repo that already pins the SDK, the packages and the
  container digest, and audits the dependency graph on every restore.

- **Security headers.** The learn pages render raw FHIR JSON and a live
  authorization code. A CSP and the usual headers cost little.

- **A coverage floor that ratchets.** The nightly floor is a number in a comment
  in `ci.yml`, raised by hand. It could instead be read from the last green run and
  bumped automatically, which is the difference between a floor that holds and one
  that quietly stops meaning anything. Needs somewhere to keep the number.

- **OpenTelemetry.** Real technique, but it needs a collector to be worth
  looking at, which is a lot of apparatus for a demo. Lowest value here.
