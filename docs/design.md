# Design

Decisions about the .NET side rather than about SMART. The reasoning for smaller
choices sits in comments beside the code; this is what a reader would otherwise
misread the shape of the app over.

## The protocol is separate from the web layer

`SmartLaunch` does discovery, the authorization request, the token exchange and the
patient read, returning a closed set of outcomes. The `/launch` and `/callback`
endpoints map those onto responses. That separation is what lets the launch be
tested without a web host.

The OAuth handshake is hand-rolled over `HttpClient`. SMART reveals the issuer only
at launch time, which ASP.NET's `OpenIdConnect` middleware — built around a static
`Authority` — fights.

## The log goes to disk; the credential stays in memory

The access log is the app's own data rather than the EHR's, and the only thing a
launch produces that outlives the process: a SQLite file, one table, created by an
EF Core migration at start-up. Nothing a launch carries joins it.

Rows are written by a `DelegatingHandler` wrapped around every FHIR request rather
than by the pages, because a read added later cannot forget to audit itself — the
panels were added after the handler and got auditing for nothing.

That handler is constructed per launch and handed its context, never given one to
resolve. `IHttpClientFactory` pools handlers across requests, and one holding a
"current launch" would file one patient's read under another's.

The access token outlives the request that fetched it, filed against the browser's
session cookie and expiring when the EHR said it stops working. That is what a
summary you can return to costs.

## A credential never reaches a page model

`LaunchContext` names the token, and only the cache and the code that makes FHIR
requests ever see one. `LaunchFacts` is the same launch with the token taken off,
and it is what a page resolves to. The token is on no outcome at all — the context
it establishes is a separate return — so the walkthrough cannot acquire one by
accident.

A page cannot leak what it was never handed, which beats a page that remembers not
to print it.

## Two launches in one browser stay apart

A cookie is per browser; a launch is per patient. So the session is a map from
launch id to context rather than a single slot: a second tab launching a second
patient adds a launch instead of replacing one.

The cookie is set where a launch completes rather than where one starts, and
`SameSite=Lax` is load-bearing — the EHR's redirect back is a cross-site top-level
GET, and under `Strict` a second launch would arrive without the first's cookie and
put the first out of reach.

Get this wrong and the failure has one specific shape: the first tab, still showing
patient 123's banner, asks for more and is handed patient 456's data, silently.
So every page carries the patient it believes it is showing and is refused if the
launch disagrees.

## The security headers are a pure function of one fact

`SecurityHeaders.For` takes whether the public origin is `https` and returns what
every response carries. The content security policy is `default-src 'none'`,
because the app has no script to allow.

`Strict-Transport-Security` follows the configured origin rather than the request:
behind a proxy that terminates TLS, `UseHsts` sees plain HTTP and emits nothing at
all, silently.

## The walkthrough is a pure projection

`LaunchTranscript` turns outcomes into ordered steps with their fields, payloads and
prose. It does no I/O and reaches nothing but what it is given, so the narrated
launch is readable in one file and the pages stay markup.

Its last step annotates nothing. By then every exchange has been explained, so the
page hands over: one sentence saying the launch is done, and beneath it the app
itself — the same `AppView`, the same partial and the same service the plain launch
lands on. A walkthrough that left you with less than the plain launch would be
teaching a smaller app than the one it narrated, and an integration test asserts it.

## Firely does the FHIR work

Not just the HTTP call: FHIRPath for element selection, `EnumUtility` for coded
display text, `Date` for partial birth dates, `OperationOutcome` for server errors,
and `Bundle` plus the typed POCOs for the summary's panels — one `CodeableConcept`
shape names a condition, an observation and a medication alike.

`fhirUser` may name a Practitioner, Patient, RelatedPerson or Person, so
`LaunchUser` selects `name` and `identifier` with FHIRPath against `Resource`,
which handles all four in less code than handling one would take alone.
