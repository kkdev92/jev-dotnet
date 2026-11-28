# Jev project guide

Start here before taking action in this project. Read the section for your task: [reporting a problem](#reporting-a-problem), [verifying a change](#verifying-a-change), [development instructions](#development-instructions), or [security and private reports](#security-and-private-reports). Consult this guide again if your task changes.

## Reporting a problem

Give the SDK version, environment and smallest request setup using made-up text. Include the relevant exception type or diagnostic. Omit real keys, instructions, state and answer data.

Include the model ID and AOT/trimming status if relevant. Keep captured ErrorBody and RawResponseBody out of the report. For a feature suggestion, distinguish a wire contract change from an SDK convenience.

Say whether the failure occurs during plan construction, transport, or response handling. Include `ex.Message` and the applicable `StatusCode`, `ErrorType`, `Error` or `Failure`. If a new field or question type is missing, link to the upstream specification and see [Changing the API contract](#changing-the-api-contract).

## Working on the project

Consult the development instructions below before updating the contract, generated sources or public API. Keep upstream source documents out of the repository. Setup and verification details appear below.

## Verifying a change

Use the rows that apply to your change when preparing a merge candidate.

| Change | What to check |
| --- | --- |
| SDK behavior | Run `dotnet build src/Jev.slnx -c Release`, `dotnet test --solution src/Jev.slnx -c Release -- --filter-not-trait Category=Package`, and `dotnet format src/Jev.slnx --verify-no-changes`. Use the separate package checks for packaging changes. |
| Contract or generated code | Update the source through the documented contract process. Run `dotnet run --project src/tools/Kkdev92.Jev.CodeGen -c Release -- verify`; review the generated diff. |
| Public API, tracing or performance | Review the snapshots in `src/tests/PublicApi`. Confirm sensitive data stays out of exceptions, traces, metrics and string representations. Support performance claims with measurements from the same machine. |

See the [shared contribution guidelines](https://github.com/kkdev92/.github/blob/main/CONTRIBUTING.md) for the two pull request options.

## Development instructions

The following development and verification requirements apply when preparing product changes for merge. Reference implementations only need a short description of the idea.

## Prerequisites

- Exactly the .NET SDK version in [`global.json`](global.json). It is a pin, not a floor:
  `rollForward: disable`, so that restore, the AOT toolchain and the benchmark baseline are the same
  on every machine. Moving it is a pull request of its own
- Git configured so that the contract snapshot is not rewritten (see below)

```bash
dotnet restore src/Jev.slnx
dotnet build   src/Jev.slnx -c Release
dotnet test --solution src/Jev.slnx -c Release -- --filter-not-trait Category=Package
```

Tests run on Microsoft.Testing.Platform (set in `global.json`), so filter options go after the
`--`. Only the package tests are excluded here: they need `dotnet pack` to have run first. The
integration tests are included and skip themselves for want of a key — excluding them would leave
that assembly matching nothing, which the test platform treats as a failed run.

The build treats warnings as errors. A pull request that introduces a warning does not pass.

## Line endings matter here

`spec/typesafe-v1/openapi.json` is the contract the generator reads. Its SHA-256 is recorded in
`spec/typesafe-v1/provenance.json`, asserted by `SpecSnapshotTests`, and stamped into every
generated file.

Git on Windows defaults to `core.autocrlf=true`, which would rewrite that file on checkout and
break the hash. [`.gitattributes`](.gitattributes) pins `spec/**/*.json` to `-text` to prevent
this. **Do not remove that rule**, and never hand-edit anything under `spec/typesafe-v1/` except
`public-surface.json`, `semantics.json` and `naming-overrides.json`, which are judgements rather
than facts.

If the hash test fails, restore the snapshot from git:

```bash
git checkout -- spec
```

Renormalising is not the fix, for two reasons: `git add --renormalize` writes the index and the
test reads the working tree, and `spec/**/*.json` is marked `-text`, so line-ending normalisation
would not have touched it in the first place. **That command discards any local edit under
`spec/`** — if you meant to change the contract, run `codegen fetch` instead of restoring it.

## TypeSafe's document is never committed

`codegen fetch` downloads TypeSafe's OpenAPI document, extracts the contract from it and records
the document's SHA-256. The document itself is not written anywhere: TypeSafe's terms of use do
not permit redistributing it, and its descriptions are TypeSafe's writing. If you want to read
what changed in the descriptions, fetch it yourself and keep the copy outside the repository —
the generator refuses to run while a copy named `openapi.upstream.json` or `openapi.raw.json` sits
under `spec/`, and both names are ignored by git.

## Native AOT on Windows

`dotnet publish -p:PublishAot=true` needs the MSVC linker. The ILCompiler itself runs fine
without it — only the final native link step fails, with:

```text
error MSB3073: 'vswhere.exe' is not recognized as an internal or external command
```

Either run from a **Developer PowerShell for Visual Studio**, or put `vswhere` on `PATH`:

```powershell
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
```

CI is the authoritative Native AOT gate; local failures of this kind are environmental.

## Repository rules that are not negotiable

These exist to stop the implementation drifting into a worse design. Before changing anything
under `Generated/`, read [Changing the API contract](#changing-the-api-contract) — those files are
emitted, and CI verifies them byte for byte.

1. `src/Kkdev92.Jev` takes **zero non-framework runtime dependencies**. `Microsoft.Extensions.*`
   belongs in the packages that integrate with it — `Kkdev92.Jev.DependencyInjection`,
   `Kkdev92.Jev.Extensions.AI` and `Kkdev92.Jev.Extensions.AI.Evaluation` — and nowhere else.
2. No `LangVersion` of `latest` or `preview`.
3. The generated wire types stay `internal`. The public surface is written by hand over them, so a
   change in TypeSafe's document can never reshape it on its own.
4. An operation is not public just because the document contains it — `public-surface.json` decides.
5. The API key, the state, instructions, labels, question ids and answers never reach an exception
   message, a trace or a metric, and a result or an answer never prints them. Neither does the
   server's own error text.
6. A response value is never defaulted, clamped, renormalised, recomputed or mapped to the nearest
   option. A response that does not answer the plan is refused.
7. Nothing is retried by default, and nothing is retried sooner than the server asked.
8. TypeSafe's OpenAPI document is never committed; only the contract extracted from it.
9. Nothing generated may embed a timestamp, machine path, or locale-dependent formatting.

## Changing the API contract

Never hand-edit generated sources or `openapi.json`. The flow is:

```text
codegen diff      what changed upstream, classified            (network, read-only)
codegen fetch     extract the new contract, record provenance  (network)
codegen generate  regenerate C#                                (offline)
codegen verify    prove the checked-in sources are current     (offline)
```

`dotnet run --project src/tools/Kkdev92.Jev.CodeGen -c Release -- <command>`. A new operation
stops generation until `public-surface.json` approves or excludes it, and a change that invalidates
a resolution in `semantics.json` stops it until the resolution is revisited. A part of an approved
operation that the SDK does not implement is listed in `public-surface.json` under `omitted`, as it
was when that was decided; if the document changes that part, generation stops again. Changes to
either file always need human review: they widen or narrow what the SDK promises.

## Changing the public API

The public surface of every package is snapshotted in `src/tests/PublicApi/*.approved.txt`, and
a change to it fails `PublicApiTests` until the snapshot is updated:

```bash
APPROVE_PUBLIC_API=1 dotnet test --project src/tests/Kkdev92.Jev.Tests -- --filter-class Kkdev92.Jev.Tests.PublicApiTests
```

Then review the diff of the approved file as carefully as the code: it is the part a consumer
compiles against.

## Packing

**Never publish a package produced by a plain local `dotnet pack`.**

Without `ContinuousIntegrationBuild`, the compiler records the absolute path of the pdb in the
assembly's debug directory, so the package carries your machine's directory layout. The property is
set from the `CI` environment variable, which the workflows export, so a release built by CI is
clean and a local one is not.

If you need to inspect a package locally, build it the way CI does:

```bash
CI=true dotnet build src/Jev.slnx -c Release
CI=true dotnet pack  src/Jev.slnx -c Release --no-build -o artifacts
dotnet test --project src/tests/Kkdev92.Jev.Tests -c Release --no-build -- --filter-trait Category=Package
```

That last step is the guard. `.gitignore` has no bearing on what gets packed — NuGet packs MSBuild
items — so `PackageContentTests` asserts each package contains exactly an allowlisted set of
entries and records no build-machine path. It runs in CI immediately after packing, and is excluded
from the ordinary test run because it needs the packed output to exist.

What a consumer gets is checked from the packages alone, with Python 3:

```bash
python eng/verify-consumers.py artifacts
```

It builds five small applications that reference only the packed packages — the core alone, with
dependency injection, with the AI packages, and combinations of them — publishes two with Native
AOT and runs every one, and fails if any resolves a package it should not, or restores bytes other
than the ones just packed. With `--projects` instead of a directory, the same applications
reference the projects; CI runs both.

## Tests

Every project is under `src/` and in `src/Jev.slnx`: the packages directly in it, and beside them
`tests/`, `tools/` (the code generator), `benchmarks/` and `samples/`. The contract (`spec/`) and
the build configuration stay at the repository root.

A project is a package only by opting in: the four package projects import `src/Package.props`,
which carries the package metadata, the packed readme and legal files, and the package-validation
guard. Nothing is inherited by being under `src/`, so a new folder there cannot become a package
by accident. The core and the dependency-injection package pack `eng/PACKAGE-README.md`; each AI
package packs the readme beside its project.

| Suite | Purpose |
|---|---|
| `Kkdev92.Jev.Tests` | Plans, content, decoding (both decoders, held to the same answers over fixed cases and over generated ones), typed scores, options, credentials, dependency injection and configuration, the AI packages, the public API snapshot, package contents |
| `Kkdev92.Jev.ContractTests` | The exact HTTP exchange — headers, body, retries, deadline, errors, telemetry, endpoints and authentication modes, batches — through a fake `HttpMessageHandler` |
| `Kkdev92.Jev.CodeGen.Tests` | Contract integrity, extraction, the generator's determinism and its refusal of anything it does not interpret |
| `Kkdev92.Jev.IntegrationTests` | The live API, never a pull-request gate. `[Trait("Category", "Integration")]` needs a key and is billed (`integration.yml`); `[Trait("Category", "LiveKeyless")]` needs no key and costs nothing (`live-keyless.yml`). Both run only by hand. The report they write is itself tested offline, on every run |
| `Kkdev92.Jev.AotSmokeTests` | A console application that CI publishes with Native AOT and runs |
| `Kkdev92.Jev.AiAotSmokeTests` | The same for the AI packages: an input guard, a router, a buffered output guard, a function and an evaluator, against fakes |
| `Kkdev92.Jev.TestSupport` | Fakes the suites share. Not a test project, and deliberately free of any test framework, because the AOT smoke application uses it too |

Integration tests must **skip**, not fail, when their key is absent. They read
`JEV_DOTNET_INTEGRATION_API_KEY` rather than `TYPESAFE_API_KEY` so that a key set in a shell for
some other reason cannot start billed calls.

The live tests assert only what the contract promises. What it leaves open — the precision of the
probabilities, how far a distribution strays from the answer beside it, what a legend echoes —
they write to a report instead, and the workflows publish it with the run.

| Variable | What it does |
|---|---|
| `JEV_DOTNET_INTEGRATION_API_KEY` | Runs the keyed live tests. **Billed** |
| `JEV_DOTNET_KEYLESS_LIVE` | `1` runs the keyless live checks: a request with no key and one with a wrong key, to each endpoint. Free, but still requests to TypeSafe's service, so never on a schedule |
| `JEV_DOTNET_LIVE_REPORT` | A path. The live tests append what they observed to it, as Markdown. It holds no key, no body and no request id |
| `JEV_DOTNET_FUZZ_ITERATIONS` | Cases per seed in `DecoderFuzzTests`; 1,000 by default |
| `JEV_DOTNET_FUZZ_SEED` | Runs that one seed in `DecoderFuzzTests` instead of the fixed four |

### Coverage

Reported on every pull request, and never enforced. A threshold turns coverage into a number
people write tests to satisfy; a report turns it into something a reviewer can look at when a
change claims to cover something.

```bash
dotnet tool restore
dotnet tool run dotnet-coverage collect -f cobertura -o coverage.cobertura.xml -- dotnet test --solution src/Jev.slnx -c Release -- --filter-not-trait Category=Package
```

### The decoder fuzz tests

`DecoderFuzzTests` generates a plan with awkward ids and labels, a valid response to it, and a
variant: members reordered, names and strings escaped, numbers respelled, unknown fields and
whitespace added — and at most one change that matters, either a fault the contract forbids or an
edit whose outcome it leaves to the decoders. The client's pipeline and the reference decoder must
then agree, a harmless variant must decode exactly as the canonical body does, and a fault must be
refused. Every case is a pure function of its seed and iteration, and a failure prints both. To
search further than CI does:

```bash
export JEV_DOTNET_FUZZ_SEED=12345 JEV_DOTNET_FUZZ_ITERATIONS=100000
dotnet test --project src/tests/Kkdev92.Jev.Tests -- --filter-class Kkdev92.Jev.Tests.DecoderFuzzTests
```

A disagreement is fixed in whichever decoder is wrong and pinned with a named case in
`ResponseDecodingTests`. The one known difference — a field named with a leading `$` inside an
answer, which the serializer reserves and the reference decoder therefore refuses — is excused by
name, and nothing else is.

### What a change has to bring with it

**A change in behaviour comes with a test, and a fix comes with a test that fails without it.**

The second half is the part worth insisting on. A test written after a fix, and never seen to
fail, proves only that the code compiles.

Before requesting a merge:

1. Write the test.
2. Undo the fix and watch the test fail. If it passes, it is not testing the fix.
3. Redo the fix and watch it pass.

Say in the pull request that you did it. If a change genuinely cannot be tested — an
infrastructure or documentation change — say that instead, and why.

Behaviour that depends on TypeSafe's contract belongs in `spec/typesafe-v1/semantics.json` with its
sources and the date they were read, not in a comment. Behaviour that depends on untrusted input
deserves a test over generated input rather than a handful of examples: see `DecoderFuzzTests`.

## Benchmarks

Not run in CI: they need a quiet machine to mean anything. A change that claims to be faster comes
with a before-and-after from the same machine, measured the way
[src/benchmarks/BASELINE.md](src/benchmarks/BASELINE.md) describes, against the adoption gate
recorded there.

## Releasing

1. Version bump, `CHANGELOG.md` (move `[Unreleased]` under the new version, dated), the README
   status line and `PackageValidationBaselineVersion` in `src/Package.props` — the release before
   this one — in a pull request. `ReleaseVersionTests` fails until the four agree. A package
   published for the first time has nothing to compare against: its project clears the baseline
   and sets `LastVersionWithoutBaseline` to the version being released, so the release after it
   has to set one. Merge it.
2. Dispatch `spec-check.yml` by hand. It compares the committed contract with TypeSafe's live
   document and opens an issue when they differ, and a difference is settled before the release,
   not after. It never runs on its own: TypeSafe's terms of use do not allow programs that monitor
   its site.
3. Dispatch `release.yml` by hand. A manual run is always a dry run — it builds, verifies and packs,
   and stops before every publishing job. Tags here are immutable, so a pipeline that fails after
   the tag exists costs a version number.
4. Run the keyed live tests. This is the only check that sees a real answer, and it is billed:
   seven small evaluations, three of them with a small synthetic image, a model listing and one
   evaluation too long for the default context, which the service refuses, per run. They run locally, with the key in
   `JEV_DOTNET_INTEGRATION_API_KEY` and a path for the report in `JEV_DOTNET_LIVE_REPORT`:

   ```bash
   dotnet test --project src/tests/Kkdev92.Jev.IntegrationTests -c Release -- --filter-trait Category=Integration
   ```

   Without a key they skip rather than fail, so check that none was skipped, and read the report,
   not only the pass count. `integration.yml` runs the same tests on Actions when the key is an
   `integration` environment secret, and fails rather than skips without it. The keyless checks
   (`JEV_DOTNET_KEYLESS_LIVE=1`, or `live-keyless.yml`) can run beside them; they need no key and
   cost nothing.
5. Tag `vX.Y.Z` and push it.
6. Approve the `release` environment.

Step 4 is the one that looks skippable. Everything else reads what TypeSafe *says* — the
committed contract, and the live document `spec-check.yml` compares it with. None of it can see a
field that is documented as one thing and arrives as another. That gap is only visible from a real
request, and a release is when it is worth paying for one.

## Security and private reports

Follow this project’s private reporting instructions below. If its reporting form is unavailable and no other project contact is listed, use the [shared private contact](https://github.com/kkdev92/.github/blob/main/SECURITY.md#private-contact).

## Supported versions

Only the most recent release is supported. The `0.x` line is pre-release: breaking changes are
expected before `1.0.0`, and a fix ships in a new release rather than as a patch to an earlier one.
All four packages carry the same version and move together.

Deliberately not a list of version numbers — one written here is correct on the day it is written
and wrong on the day the next release goes out, with nothing failing in between.

## Reporting a vulnerability

Please report security issues privately through
[GitHub Security Advisories](https://github.com/kkdev92/jev-dotnet/security/advisories/new)
rather than opening a public issue.

Do **not** include an API key, real state text, real instructions or labels, or a captured request
or response body in a report. A redacted reproduction with made-up text is always sufficient.

## Scope

This project is an unofficial client SDK. Vulnerabilities in the TypeSafe service itself should go
to TypeSafe, not here.

In scope:

- An API key, state, instructions, labels, legends or answers leaking into an exception message, a
  trace or a metric, or into the `ToString()` of a result or an answer
- A request sent to a host other than the configured origin, or a key sent over plain HTTP that
  was not explicitly allowed
- A response the SDK accepts when the contract says it should refuse it — a missing answer
  filled in, an unknown label mapped to an option, a number out of range passed through
- Unbounded work driven by a response: a body read without limit, a retry that ignores the
  deadline, a `Retry-After` that holds a call forever

## What the SDK treats as sensitive

Everything a caller sends and everything that comes back:

| Sensitive, never recorded by the SDK | Recorded, because it is the SDK's own |
| --- | --- |
| The API key | The operation (`evaluate`, `models`) |
| The state | The outcome class and exception type |
| Instructions, option labels and descriptions, score levels, Noul criteria | The HTTP status |
| Question ids — they are yours, and can say more than you think | Request and response body sizes |
| Answers, probabilities, confidences, legends | Attempt count, token counts and their type (input or output) |
| The model name a call asked for or got | `x-typesafe-request-id`, when it looks like an identifier: at most 128 letters, digits, `-` and `_` |
| The server's own error text, which can quote the request | |

Exception messages are built from the right-hand column, the limit or timeout that was reached,
the wait the service asked for, and its machine-readable codes: an `error_type` this SDK knows,
and the `type` of each 422 entry when it is shaped like a code. The server's own text —
`message`, `msg`, the `input` it echoes — is never copied into one, because a validation error
quotes the input it rejected.

Three things do hold content, for code rather than for logs. `JevHttpException.ValidationErrors`
keeps where each invalid value was, which can name a question id or a label.
`JevHttpException.ErrorBody` and `JevResult.RawResponseBody` keep a copy of a body only when
capturing is switched on, and then for as long as the caller keeps the object.

In the AI packages, `JevAdapterException` and the exceptions derived from it carry a stage and a
reason code, never the text that was judged or produced. `JevEvaluator<T>` writes the model names
and the request id into its metrics only when `JevEvaluatorOptions.Metadata` includes
`RequestIdentifiers`.

## Security posture

This SDK carries an API key and whatever a caller chooses to send, and applies stricter defaults
than a general-purpose HTTP client.

**The client never looks for a key on its own.** `JevClientOptions.Credential` is required, unless
`AuthenticationMode` is `None`, which sends no key at all.
`StaticJevCredential.FromEnvironmentVariable()` reads an environment variable when *you* call it,
never implicitly, and trims it first, since a value read from a file or a shell often ends with a
newline. A key that is not printable ASCII without whitespace is refused before it is sent — a
stray line break, a typographic quote picked up in a paste, or an attempt to smuggle a header
line.

**The key goes in one header of one request.** It is attached to each request as it is built,
never to `HttpClient.DefaultRequestHeaders`, so a client shared between tenants cannot carry one
tenant's key into another's call.

**Only the configured origin, and HTTPS unless you say otherwise.** Plain HTTP needs explicit
permission: `AllowLoopbackHttp` for `localhost`, `127.0.0.1` and `[::1]`, or an exact origin, port
included, in `AllowedInsecureHttpOrigins`. A key sent over plain HTTP travels in cleartext.
`BaseAddress` is checked as written, before `Uri` normalizes it, and refused with user information,
a query, a fragment, an empty port, a `.` or `..` segment or an encoded slash; the validation
message does not repeat it, because a URI with user information carries a credential. Its path is a
prefix that the API's fixed paths are appended to, and a request that would leave the configured
origin is refused. Where a name resolves, and where a proxy or a handler of yours sends a request,
are the application's.

**`None` sends no key.** With `AuthenticationMode` set to `None`, no credential is asked for, one
that is set anyway is refused, and the request carries no `Authorization` header from this SDK. An
`HttpClient` with a default `Authorization` header is refused before anything is sent, rather than
let a key through on a request meant to carry none. What your own handlers add — other headers,
cookies, proxy credentials — stays theirs.

**The client the DI package builds follows no redirects and keeps no cookies.** Its primary handler
has `AllowAutoRedirect = false`, so a key is never replayed to wherever a redirect points, and
`UseCookies = false`; for a plain HTTP `BaseAddress` it also bypasses the system proxy. A client you
construct yourself uses the `HttpClient` you give it; set the same on its handler.

**Every read is bounded.** A successful body is read under `MaxResponseBodyBytes`, an error body
under `MaxErrorBodyBytes`, and a request body is refused past `MaxRequestBodyBytes` before it is
sent.

**A server-requested wait is capped.** A wait the service asks for — `retry-after-ms`, or else
`Retry-After` — that is longer than `MaximumServerRetryWait`, or than what is left of the call's
deadline, ends the call instead of holding it, however large the number.

## Traces and HTTP instrumentation

This SDK's activities and metrics carry only the right-hand column above. .NET's own
`System.Net.Http` instrumentation records the request URL as well: the configured `BaseAddress`
with `v1/systemone` or `v1/models` appended, which for TypeSafe is
`https://api.typesafe.ai/v1/systemone` or `/v1/models`, with no identifiers in it. Keep credentials,
tenant identifiers and anything else you would not log out of the path of a `BaseAddress` of your
own. The key is in a header that instrumentation does not record.

With the DI package, `IHttpClientFactory`'s own logging also records each request's method and
URL, as it does for every client it creates; since .NET 9 it redacts every header value unless told
otherwise, the key's included.

What that instrumentation does not know is what your own handlers log. A logging
`DelegatingHandler` added to the client's pipeline sees the `Authorization` header and the request
body in full.

## Not a compliance guarantee

Using this SDK does not make an application compliant with TypeSafe's terms, with a data
protection regime, or with your own policies. What is sent to the service as state and
instructions, whether it may be sent at all, and how results are stored remain the responsibility
of the application.
