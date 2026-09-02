# Configuration

Everything lives in `appsettings.json`, and every key can be overridden by
environment variable in the usual ASP.NET Core way (`Smart__PublicOrigin`).

| Key | Default | What it is |
| --- | --- | --- |
| `Smart:PublicOrigin` | `http://localhost:5000` | The address readers reach this app on. Required. |
| `Smart:TrustedIssuers` | the public launcher | The EHRs a launch may come from. Required, and an empty list trusts nobody. |
| `Smart:Scopes` | `openid fhirUser user/Practitioner.read` plus three `patient/` scopes | What the app asks each EHR for. |
| `ConnectionStrings:AccessLog` | `app.db` beside the app | The SQLite access log, migrated on every start. |
| `DataProtection:KeyRing` | `keys/` beside the app | Signs `/learn`'s exchange form. |

`GET /up` answers 200 and nothing else. It is kamal-proxy's default health-check
path, so a deployment configures none.

## Launching from another EHR

Add its issuer to the allowlist — the app refuses launches from anywhere not on it:

```json
"Smart": {
  "TrustedIssuers": [ "https://launch.smarthealthit.org", "https://ehr.example" ]
}
```

## PublicOrigin behind a proxy

The app is told its origin rather than reading one off the incoming request, so
every URL it hands an EHR is one a browser can come back to. Behind a proxy that
terminates TLS, set it to the public `https://` origin; nothing else changes.

Get it wrong and the session cookie's `Secure` flag follows the wrong scheme,
which is not an outage — the launch still works — but the cookie authenticating a
patient summary is then marked as safe to send in the clear.

## Scopes

`openid fhirUser` is what makes an EHR say who started the launch, and
`user/Practitioner.read` is what lets that person's name be read. The three
`patient/` scopes are the summary's panels.

Drop any of them and the launch still works: the summary says nobody was named, or
the panel says the EHR would not answer. They are v1 syntax —
`patient/Condition.read`, not `patient/Condition.rs`.

## The two directories

`app.db` is the access log, and deleting it loses nothing but the log. The `keys/`
ring is kept rather than minted at every boot because a reader paused mid-exchange
across a restart would otherwise fail with an error naming nothing useful. Nothing
encrypts the keys at rest — every option for that means holding a second key
somewhere — so the app warns on every start and the directory's permissions are
what protect them.

A deployment points both at one volume; see [deploying.md](deploying.md).
