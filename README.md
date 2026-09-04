# dotsmoke

A minimal SMART on FHIR app that handles a standard EHR launch end to end, walking
you through it a step at a time while it happens, and ending on a short summary of
the patient in context.

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

The launch is real; the pauses are the only thing a production app would not do. It
stops at each step to show what was sent and what came back, and hands over to the
app itself at the end.

To run it locally instead:

```bash
dotnet run --project src/SmartOnFhirDemo
```

Then use `http://localhost:5000/learn` as the launch URL. See
[docs/configuration.md](docs/configuration.md) for the settings, including how to
launch from an EHR other than the public launcher.

## The launch flow

```
SMART Launcher ──GET /learn?iss=…&launch=…──▶ app
  app ──GET {iss}/.well-known/smart-configuration──▶ authorize + token endpoints
  app  ① ② ③  explain what was sent, what came back, and what is about to go out
  reader ──▶ {authorize}?…&aud={iss}&launch=…&code_challenge=…
  launcher (provider login → patient/consent) ──302──▶ app /learn/callback?code=…&state=…
  app  ④  the code has arrived and has not been spent — the reader presses exchange
  app ──POST {token}──▶ { access_token, id_token, patient }
  app  validate id_token against the keys at {jwks_uri}
  app ──GET {iss}/Patient/{id}   Bearer──▶ Patient
  app ──GET {iss}/{fhirUser}     Bearer──▶ Practitioner
  app  file the launch against the browser's session cookie
  app ──302──▶ /learn/token?id={launchId}&patient={id}   ⑤ ⑥ ⑦
  reader ──GET /learn/patient?…──▶ app  ⑧  ──▶ the app itself
  reader ──GET /learn/patient?…&show=conditions──▶ app
  app ──GET {iss}/Condition?patient={id}  Bearer──▶ Bundle  ──▶ the panel
```

The exchange is a form post rather than something the callback does on arrival: it
is the one step a reader gets to press, and stopping there is what makes an
unspent authorization code visible. Landing afterwards on a URL naming the launch
keeps the code out of the address bar and stops a refresh re-sending a spent one.

## The app it ends on

Step ⑧ stops narrating and hands over to the app the handshake was for. That URL
keeps working until the EHR's token runs out. Three panels read on from it — conditions, vital signs and medications —
each a plain link to a URL naming the panel, so every one of them is shareable and
the back button works.

Pressing one swaps the panel in place rather than reloading the page. That is the
app's only JavaScript, and it is an enhancement: the page asks itself for the panel
alone, and the same markup a navigation would have rendered goes into the same
element. With the script blocked or still loading, the links navigate and the app is
unchanged.

Those reads are searches rather than reads by id: `Condition?patient={id}`, not
`Condition/{id}`. That is what a `patient/Condition.read` scope authorises — a
class of data about one patient, not one URL.

Every panel degrades to a sentence. A patient with nothing recorded says so, and a
scope the EHR declined to grant says that instead — an empty list and a refusal
look identical on screen and mean opposite things.

Worth knowing before you read too much into a green run: the SMART App Launcher
does not enforce scopes the way a real EHR does. A launch against it shows the
searches working; it shows rather less about what happens when a scope is refused.

## What SMART demands

Four things the protocol makes an app responsible for. The walkthrough shows all
four happening.

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
