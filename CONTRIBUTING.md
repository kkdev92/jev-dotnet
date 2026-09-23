# Contributing

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
   belongs in `Kkdev92.Jev.DependencyInjection` and nowhere else.
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
a resolution in `semantics.json` stops it until the resolution is revisited. Changes to either file
always need human review: they widen or narrow what the SDK promises.

## Changing the public API

The public surface of both packages is snapshotted in `src/tests/PublicApi/*.approved.txt`, and
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

## Tests

Every project is under `src/` and in `src/Jev.slnx`: the packages directly in it, and beside them
`tests/`, `tools/` (the code generator), `benchmarks/` and `samples/`. The contract (`spec/`) and
the build configuration stay at the repository root.

A project is a package only by opting in: the two package projects import `src/Package.props`,
which carries the package metadata, the packed readme and legal files, and the package-validation
guard. Nothing is inherited by being under `src/`, so a new folder there cannot become a package
by accident.

| Suite | Purpose |
|---|---|
| `Kkdev92.Jev.Tests` | Plans, content, decoding (both decoders, held to the same answers over fixed cases and over generated ones), options, credentials, dependency injection, the public API snapshot, package contents |
| `Kkdev92.Jev.ContractTests` | The exact HTTP exchange — headers, body, retries, deadline, errors, telemetry — through a fake `HttpMessageHandler` |
| `Kkdev92.Jev.CodeGen.Tests` | Contract integrity, extraction, the generator's determinism and its refusal of anything it does not interpret |
| `Kkdev92.Jev.IntegrationTests` | The live API, never a pull-request gate. `[Trait("Category", "Integration")]` needs a key and is billed (`integration.yml`); `[Trait("Category", "LiveKeyless")]` needs no key and costs nothing (`live-keyless.yml`). Both run only by hand. The report they write is itself tested offline, on every run |
| `Kkdev92.Jev.AotSmokeTests` | A console application that CI publishes with Native AOT and runs |
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

So, before opening the pull request:

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
   this one, and none for the first — in a pull request. `ReleaseVersionTests` fails until the four
   agree. Merge it.
2. Dispatch `spec-check.yml` by hand. It compares the committed contract with TypeSafe's live
   document and opens an issue when they differ, and a difference is settled before the release,
   not after. It never runs on its own: TypeSafe's terms of use do not allow programs that monitor
   its site.
3. Dispatch `release.yml` by hand. A manual run is always a dry run — it builds, verifies and packs,
   and stops before every publishing job. Tags here are immutable, so a pipeline that fails after
   the tag exists costs a version number.
4. Dispatch `integration.yml` by hand, with the `JEV_DOTNET_INTEGRATION_API_KEY` secret. This is
   the only check that sees a real answer, and it is billed: three small evaluations and a model
   listing per run. Read the report in the run summary, not only the tick. `live-keyless.yml` can
   run beside it; it needs no key and costs nothing.
5. Tag `vX.Y.Z` and push it.
6. Approve the `release` environment.

Step 4 is the one that looks skippable. Everything else reads what TypeSafe *says* — the
committed contract, and the live document `spec-check.yml` compares it with. None of it can see a
field that is documented as one thing and arrives as another. That gap is only visible from a real
request, and a release is when it is worth paying for one.
