# dotsmoke

README.md is the front door: what this app is, how to launch it, and what SMART
demands. `docs/` covers the rest — configuration, design, development (tests, CI,
formatting, analyzers, the toolchain) and deploying. This file is only what
neither says.

## Branching

`main` is protected: pull requests only, with the `Format, analyzers, tests`
check green. Never commit to `main` — branch, commit there, and leave opening
the pull request to me.

## Commits

Run the CI sequence in `docs/development.md` locally first and report what it
said. Run `dotnet csharpier format .` before checking, or your own edits will
fail it.

Tooling and the churn it implies are separate commits, and code lands before
the gate that would reject it (682ddee, 348bdf0, ad4fca6). A commit that turns
a check on should be green on arrival.

When a change makes a statement in README.md or `docs/` false, fix it in the
same commit. Both are for readers: record *why* in a comment beside the code, not
in new prose there.

## Settled — don't re-litigate without new evidence

- **In-box analyzers only.** Meziantou, Sonar and Roslynator were measured;
  Meziantou needed five rules disabled to yield four findings here, three of
  them regex timeouts in tests.
- **`rollForward: latestFeature`, not `disable`.** The docs advise `disable`
  alongside lock files, but that is about SDK-injected packages (ILLink,
  trimming); these lock files contain none.
- **Never make the `integration` job a required check.** It does not run on
  `pull_request`, so requiring it would block every PR permanently.

## Traps

`Directory.Build.props` is comment-heavy, and `--` is illegal inside an XML
comment: it fails as `MSB4181 ... returned false but did not log an error`,
naming neither the file nor the cause.

`IHttpClientFactory` pools message handlers for two minutes and gives each its
own DI scope, one that is not the request's and outlives it. Anything
per-request cached inside a handler is therefore served to later, unrelated
requests — for the access log that means attributing one patient's read to
another launch, silently and with nothing in the log saying so.
`AccessLogHandler` takes its launch as a constructor argument for exactly this
reason, and is built at the call site rather than registered.

This app has no forwarded-headers middleware, and adding one would be a regression.
Behind a TLS-terminating proxy every conventional fix is wrong in its own way:
kamal-proxy suppresses `X-Forwarded-*` by default when it terminates TLS; since
ASP.NET Core 8.0.17 the middleware ignores those headers from any proxy not
explicitly trusted, and its defaults are loopback only, which a proxy in another
container is not; `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` — the near-universal
workaround, recommended by published .NET Kamal templates — works by *clearing* the
trust list; and setting `KnownNetworks` to the Docker bridge CIDR hard-codes a
Docker-assigned fact into the app, failing as a redirect loop when it drifts.
`Smart:PublicOrigin` sidesteps all four: the app is told its origin and builds every
absolute URL and the session cookie's `Secure` flag from it. The failure it prevents
is not an outage — the launch works either way — but the cookie authenticating a
patient summary being marked as safe to send in clear, silently.

Kamal reaches servers through net-ssh, not the `ssh` binary, and net-ssh looks
only in `ssh-agent` and at the default identity filenames — `id_rsa`, `id_dsa`,
`id_ecdsa`, `id_ed25519`. A key at any other path is invisible to it however well
`ssh -i` works, and the symptom is an authentication failure that reads as a
server problem.

Publishing for a runtime the project does not name in `RuntimeIdentifiers`
rewrites the lock file as a side effect. The next `--locked-mode` restore then
fails with `NU1004`, having been handed a lock file describing a project it no
longer matches. This is why the app's lock file names `net10.0/linux-x64` beside
the plain target — the native SQLite binary resolves only there.

A `/data` mount not owned by the image's UID 1654 stops the start-up migration
before the app serves, so `/up` never answers and Kamal aborts with the previous
container still running. It reads as a health-check timeout rather than as a
permissions problem. Read the UID from the image rather than assuming it —
published sources disagree and `dotnet-docker` has changed it before:
`docker inspect <image> --format '{{.Config.User}}'`.

Comments here say *why*, not *what*. Test names are sentences.
