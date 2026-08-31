# dotsmoke

README.md covers architecture, running, tests, formatting, analyzers, toolchain
pins and CI. This file is only what it does not say.

## Branching

`main` is protected: pull requests only, with the `Format, analyzers, tests`
check green. Never commit to `main` — branch, commit there, and leave opening
the pull request to me.

## Commits

Run the README's CI sequence locally first and report what it said. Run
`dotnet csharpier format .` before checking, or your own edits will fail it.

Tooling and the churn it implies are separate commits, and code lands before
the gate that would reject it (682ddee, 348bdf0, ad4fca6). A commit that turns
a check on should be green on arrival.

When a change makes a README statement false, fix it in the same commit.

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

Comments here say *why*, not *what*. Test names are sentences.
