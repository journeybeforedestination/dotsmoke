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

One direct package: `Hl7.Fhir.R4` 6.4.0 (BSD-3-Clause). Everything else is in-box
ASP.NET Core.

## License

MIT — see [LICENSE](LICENSE).
