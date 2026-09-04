# Design

Decisions about the .NET side rather than about SMART. The reasoning for smaller
choices sits in comments beside the code; this is what a reader would otherwise
misread the shape of the app over.

## The protocol is separate from the web layer

`SmartLaunch` does discovery, the authorization request, the token exchange and the
patient read, returning a closed set of outcomes. The `/learn` pages map those onto
responses and onto the prose that narrates them. That separation is what lets the
launch be tested without a web host.

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
walkthrough you can step through, and an app you can return to, costs.

The log is also something the app shows, at the end of the walkthrough: a launch
can expand its own rows. Scoped by launch id rather than by issuer and patient,
which is why the column exists — the wider scope would show one clinician the times
and panels another had read from the same chart, from a page that never asked
whether they may know that. `AccessLog` writes and `AccessLogReader` reads; keeping
them apart is what lets the writing seam stay unable to query. The question the
older index answers, "who has been in this chart, lately", the app answers to
nobody: it needs an audience this app cannot authenticate.

## The app bounds its own traffic, not the EHR's

`/learn` is a URL a stranger can open, and every launch drives requests at someone
else's server — for the shipped configuration, a public sandbox with no SLA. One
`DelegatingHandler` on every HTTP client holds this app to four requests in flight
at a time; a fifth waits five seconds for a slot and is then refused as a server
that could not be reached. The numbers and their reasons are in `EhrTraffic`.

The bound is on the calls this app makes rather than on the requests it receives,
and that is the whole design. An inbound per-IP limit on `/learn` bounds one
launch; the panel reads that follow come from an established session and never
touch `/learn` again. Bounding the calls themselves is the only place that
catches both.

Concurrency rather than a rate, because it self-regulates: an EHR that slows down
makes in-flight calls pile up, and this app backs off without being told anything
is wrong. A rate cap keeps sending at the same rate into a server that has started
timing out.

The limiter is one object for the process, handed to the handler. `IHttpClientFactory`
pools handler chains and scopes each one separately, so a handler that built its own
would cap four *per chain* — the mirror of the access log handler's rule, and the
reason both are constructor arguments.

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
every response carries. The content security policy is `default-src 'none'` and then
three names back: `style-src` for the layout's one style block, and `script-src
'self'` plus `connect-src 'self'` for `wwwroot/app.js` and the pane it fetches. Both
are `'self'` rather than a hash because the script is a file — nothing inline is ever
allowed to run, which is the relaxation that would have mattered.

`Strict-Transport-Security` follows the configured origin rather than the request:
behind a proxy that terminates TLS, `UseHsts` sees plain HTTP and emits nothing at
all, silently.

## A tab swaps the pane, or navigates

`_App.cshtml` renders the tabs as plain links and the pane through `_Pane.cshtml`.
`/learn/patient` answers `?handler=pane` with that same partial alone, so a swap and
a navigation cannot render different chart panels — there is one piece of markup and
it does not know which happened.

The handler resolves the launch through the same guard the page does. Skipping it
would have made the pane a second way to read a patient the launch does not name,
which is the whole thing the page's own check exists to stop.

A refusal answers with a status rather than a redirect, and the script's fallback is
to navigate for real and land on `/error`. Swapping the refusal into the pane instead
would leave the banner above it still naming a patient whose launch is gone.

`?handler=access` renders the access section the same way, through the same guard,
and the script refreshes it after the pane rather than beside it: reading a panel is
itself a logged request, so a section fetched in parallel would race the row it
exists to show. Without the refresh the log would be right on arrival and silently
wrong from the first tab press — missing the one request the reader just caused.

The swap replaces the contents of a div inside the section's `<details>` rather than
the `<details>` itself, which would reset `open` and collapse the log under whoever
had just expanded it.

## The walkthrough is a pure projection

`LaunchTranscript` turns outcomes into ordered steps with their fields, payloads and
prose. It does no I/O and reaches nothing but what it is given, so the narrated
launch is readable in one file and the pages stay markup.

Its last step annotates nothing. By then every exchange has been explained, so the
page hands over: one sentence saying the launch is done, and beneath it the app
itself — the banner, the panels the token allows, and the resource behind them. The
narration stops; the app it was narrating is what is left, and an integration test
asserts that it still reads on from the launch.

## Firely does the FHIR work

Not just the HTTP call: FHIRPath for element selection, `EnumUtility` for coded
display text, `Date` for partial birth dates, `OperationOutcome` for server errors,
and `Bundle` plus the typed POCOs for the summary's panels — one `CodeableConcept`
shape names a condition, an observation and a medication alike.

`fhirUser` may name a Practitioner, Patient, RelatedPerson or Person, so
`LaunchUser` selects `name` and `identifier` with FHIRPath against `Resource`,
which handles all four in less code than handling one would take alone.
