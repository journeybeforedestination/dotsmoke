# Development

## Tests

```bash
dotnet test                                              # everything
dotnet test --project tests/SmartOnFhirDemo.UnitTests    # just the fast ones
```

The unit tests cover the pure code and need nothing but the SDK. The integration
tests host the app in memory and drive it through a real SMART launch; most of them
need the [SMART App Launcher][launcher] running:

```bash
docker run -d --name smart-launcher -p 8080:80 \
  smartonfhir/smart-launcher-2@sha256:72bd3e3c682ce4c74e6dddb605d89acad7c8aae446ae38079e8dfe8455b84793

SMART_LAUNCHER_URL=http://localhost:8080 dotnet test
```

Prefix `docker` with `sudo` on a host that keeps its users out of the
root-equivalent `docker` group. The image is pinned by digest because the launcher
publishes only a `latest` tag.

Without `SMART_LAUNCHER_URL` those tests skip and the rest still run, so
`dotnet test` stays green on a machine with no launcher. The launcher proxies the
public `r4.smarthealthit.org` sandbox, so they need an internet connection and
assert on the shape of the rendered summary rather than on specific patient data.
Point it at your own FHIR server with `FHIR_SERVER_R4` to go hermetic.

Coverage locally:

```bash
dotnet test --coverage --coverage-settings coverage.config \
  --coverage-output-format cobertura
./.github/coverage.sh
```

[launcher]: https://github.com/smart-on-fhir/smart-launcher-v2

## CI

`.github/workflows/ci.yml` runs on every push and pull request, and its comments say
why each step is there:

```bash
dotnet tool restore
dotnet csharpier check .
dotnet restore --locked-mode
dotnet build --no-restore -warnaserror
dotnet ef migrations has-pending-model-changes \
  --project src/SmartOnFhirDemo --no-build
dotnet test --no-build --coverage --coverage-settings coverage.config \
  --coverage-output-format cobertura
./.github/coverage.sh
```

The whole job runs offline, and no database is provisioned. The launcher-bound
tests run nightly in a second job, which is where the coverage floor lives.

The image is built by the SDK's `PublishContainer` target rather than from a
Dockerfile, pushed to `ghcr.io/journeybeforedestination/dotsmoke` and tagged with
the commit. To look at one locally, where a daemon is needed:

```bash
dotnet publish src/SmartOnFhirDemo --os linux --arch x64 /t:PublishContainer \
  -p ContainerArchiveOutputPath=./image.tar.gz
```

Each image carries a signed build-provenance attestation, filed against this
repository rather than the registry:

```bash
gh attestation verify oci://ghcr.io/journeybeforedestination/dotsmoke:<commit> \
  --repo journeybeforedestination/dotsmoke
```

## Formatting

[CSharpier][csharpier] owns the layout of every `.cs`, `.csproj`, `.props` and
`.config` file here, pinned in `.config/dotnet-tools.json`:

```bash
dotnet tool restore
dotnet csharpier format .   # apply
dotnet csharpier check .    # verify, changing nothing
```

`.editorconfig` covers what CSharpier does not — naming, `var`, pattern matching —
as suggestions rather than gates. `.cshtml` is formatted by hand; CSharpier has no
Razor support.

The tree-wide reformat is listed in `.git-blame-ignore-revs`, which GitHub applies
by itself; locally, `git config blame.ignoreRevsFile .git-blame-ignore-revs`.

[csharpier]: https://csharpier.com/

## Analyzers

No third-party analyzer package. `Directory.Build.props` turns the in-box Roslyn
analyzers up — `AnalysisLevel` at `10.0-recommended`, the security category at
`All`, code style enforced in the build. Warnings while you type, errors in CI:
`dotnet build -warnaserror` is the gate. Three rules are overridden in
`.editorconfig`, each with its reason beside it.

The dependency audit comes free with that gate: NuGet checks the resolved graph
against its advisory database on every restore, so a newly disclosed advisory can
turn a build red with no commit behind it. That is the intent — this app handles
OAuth credentials and patient data.

## The pinned toolchain

`global.json` pins the SDK to 10.0.400 and rolls forward to any later 10.0.x. A
`packages.lock.json` beside each project pins the resolved dependency graph.
Adding or upgrading a package rewrites the lock file during `dotnet restore` —
commit it with the change. CI restores with `--locked-mode`, which fails rather
than quietly resolving something new.

## Dependencies

The app has three direct packages: `Hl7.Fhir.R4` (BSD-3-Clause) for the FHIR work,
`Microsoft.IdentityModel.JsonWebTokens` (MIT) to validate the id_token, and
`Microsoft.EntityFrameworkCore.Sqlite` (MIT) for the access log. Everything else is
in-box ASP.NET Core.

The second is not hand-rolled deliberately: verifying a JWS against a published
JWKS is easy to write and easy to write wrongly, and getting it wrong is silent.

The tests add `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing` and
`Microsoft.Testing.Extensions.CodeCoverage` — no mocking library, no container
library. `.config/dotnet-tools.json` pins CSharpier, `dotnet-coverage` and
`dotnet-ef`. Kamal is a Ruby gem, installed by the deploy job and used nowhere else.

`.github/dependabot.yml` proposes NuGet and action updates weekly. It does not cover
`.config/dotnet-tools.json`, so those pins are bumped by hand.
