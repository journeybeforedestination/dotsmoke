# dotsmoke

A minimal SMART on FHIR app that handles a standard EHR launch end to end, then
renders a short summary of the patient in context — and, on a second launch URL,
walks you through the same launch a step at a time while it happens.

Built against the public [SMART App Launcher](https://launch.smarthealthit.org/),
on .NET 10, ASP.NET Core Razor Pages, and the
[Firely SDK](https://github.com/FirelyTeam/firely-net-sdk).

## Try it

Nothing to install. Open the [SMART App Launcher](https://launch.smarthealthit.org/)
and fill in:

| Field | Value |
| --- | --- |
| Launch Type | Provider EHR Launch |
| FHIR Version | R4 |
| App's Launch URL | `https://dotsmoke.wastebook.dev/learn` |
| Client ID, Redirect URIs | leave blank |

Pick a patient, press **Launch**.

`/learn` stops at each step to show what was sent and what came back. Swap it for
`/launch` to run the same handshake without stopping and land straight on the
summary — which is how a real app behaves.

To run it locally instead:

```bash
dotnet run --project src/SmartOnFhirDemo
```

Then use `http://localhost:5000/learn` as the launch URL. See
[docs/configuration.md](docs/configuration.md) for the settings, including how to
launch from an EHR other than the public launcher.

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

`/callback` renders nothing. It exchanges the code, files what it got against the
browser, and redirects to a URL naming the launch — which keeps the authorization
code out of the address bar and stops a refresh from re-sending a spent code.

## The summary

The launch lands on `/summary`, and that URL keeps working until the EHR's token
runs out. Three panels read on from it — conditions, vital signs and medications —
each a plain link, because this app has no JavaScript at all.

Those reads are searches rather than reads by id: `Condition?patient={id}`, not
`Condition/{id}`. That is what a `patient/Condition.read` scope authorises — a
class of data about one patient, not one URL.

Every panel degrades to a sentence. A patient with nothing recorded says so, and a
scope the EHR declined to grant says that instead — an empty list and a refusal
look identical on screen and mean opposite things.

## The same launch, narrated

`/learn` runs the identical protocol against the identical EHR and opens the
identical session, so it ends on the same summary with the same panels. It differs
only in stopping where the plain launch redirects, to explain what was exchanged
before going on — eight steps, carried across the top of every page.

Worth knowing before you read too much into a green run: the SMART App Launcher
does not enforce scopes the way a real EHR does. A launch against it shows the
searches working; it shows rather less about what happens when a scope is refused.

## What SMART demands

Four things the protocol makes an app responsible for. `/learn` shows all four
happening.

- **The issuer is checked against an allowlist.** `iss` arrives as a query
  parameter and everything downstream trusts it. Unchecked, that is a server-side
  request forgery, an open redirect, and a way to harvest authorization codes. The
  endpoints that issuer publishes are held to its origin too: an allowlist matches an
  origin, and the path beneath one is the EHR's to choose.
- **The id_token is validated, though it need not be.** OIDC Core 3.1.3.7 lets an
  app skip signature validation when the token arrives over direct TLS to the token
  endpoint, which is how it arrives here. This app checks it anyway, against the
  EHR's published keys.
- **A fhirUser pointing elsewhere is not followed.** Following an absolute
  reference to another origin would send this launch's access token to a server it
  was never issued for.
- **Identity degrades, it does not fail.** No `openid` grant, no published
  `jwks_uri`, or a token that fails validation each leave the launch standing with
  a sentence saying why nobody is named. The app's job is the patient summary.

## Docs

- [Configuration](docs/configuration.md) — settings, and the two that bite
- [Design](docs/design.md) — how the code is arranged, and why
- [Development](docs/development.md) — tests, CI, formatting, the toolchain
- [Deploying](docs/deploying.md) — the public instance, and how it gets there

## License

MIT — see [LICENSE](LICENSE).
