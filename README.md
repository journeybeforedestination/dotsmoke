# dotsmoke

A minimal SMART on FHIR app that handles a standard EHR launch end to end, then
renders a short summary of the patient in context.

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

## Running it

```bash
dotnet run --project src/SmartOnFhirDemo
```

Then at [launch.smarthealthit.org](https://launch.smarthealthit.org/):

| Field | Value |
| --- | --- |
| Launch Type | Provider EHR Launch |
| FHIR Version | R4 |
| App's Launch URL | `http://localhost:5000/launch` |
| Client ID, Redirect URIs | leave blank |

Pick a patient, press **Launch**.

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

## Design notes

- **The OAuth handshake is hand-rolled** over `HttpClient`. SMART reveals the
  issuer only at launch time, which ASP.NET's `OpenIdConnect` middleware — built
  around a static `Authority` — fights.
- **Nothing is persisted.** Only the issuer and PKCE verifier survive the
  redirect, held in `IMemoryCache` under the OAuth `state` for five minutes. The
  access token is used once and discarded, so `/callback` renders in the same
  request that exchanges the code.
- **Firely does the FHIR work**, not just the HTTP call: FHIRPath for element
  selection, `EnumUtility` for coded display text, `Date` for partial birth
  dates, `OperationOutcome` for server errors.

## Dependencies

The app has one direct package: `Hl7.Fhir.R4` 6.4.0 (BSD-3-Clause). Everything
else is in-box ASP.NET Core.

The tests add `xunit.v3` and `Microsoft.AspNetCore.Mvc.Testing`, and nothing else
— no mocking library, no container library. `global.json` opts `dotnet test` into
Microsoft.Testing.Platform, which the .NET 10 SDK requires.

## License

MIT — see [LICENSE](LICENSE).
