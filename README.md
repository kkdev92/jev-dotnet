# Kkdev92.Jev

[![NuGet](https://img.shields.io/nuget/v/Kkdev92.Jev)](https://www.nuget.org/packages/Kkdev92.Jev)
[![CI](https://github.com/kkdev92/jev-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/kkdev92/jev-dotnet/actions)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/14773/badge)](https://www.bestpractices.dev/projects/14773)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)

An unofficial .NET 10 SDK for Jev, TypeSafe's System One API. You declare your questions once, as
a typed decision plan; the SDK validates and encodes them once and reuses the bytes on every call,
and the answers come back through typed handles — after the response has been checked against the
contract, not before.
_Built for applications that make a decision from every call, and would rather a malformed answer
failed loudly than turned into a default._

> **Status:** `0.1.1-alpha`. **Run against the live service with an API key on 2026-09-25:** the
> live tests ([integration.yml](.github/workflows/integration.yml)) pass — every question kind, a
> structured state, structured levels, a question without instructions, the model list — and the
> `400`, `401`, `403` and `422` bodies the service sends are the ones the SDK reads.
>
> Every call path is also exercised offline, against hand-written fakes of the HTTP API shaped after
> TypeSafe's own OpenAPI document. What no call has shown yet — a real `429` or `529` — is listed
> under [Known Limitations](#known-limitations).

---

## Table of Contents

- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Why Kkdev92.Jev](#why-kkdev92jev)
- [Usage](#usage)
- [What Is Guaranteed](#what-is-guaranteed)
- [Known Limitations](#known-limitations)
- [How It Works](#how-it-works)
- [Platform Requirements](#platform-requirements)
- [Security and Privacy](#security-and-privacy)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [Support & Maintenance Policy](#support--maintenance-policy)
- [License](#license)
- [Acknowledgments](#acknowledgments)

---

## Features

- **Typed Decision Plans**: questions are validated and encoded once, when the plan is built; every call copies the encoded bytes into its request instead of serializing the questions again
- **Typed Answers**: a choice maps back to your own value — an enum, a string, a struct — through the handle its question returned, and a handle from another plan is refused rather than read
- **Strict Decoding**: a `200` is not accepted on faith. A missing, extra, duplicated or mistyped answer, a label nobody asked about, or a probability of 1.5 fails the call with `JevProtocolException` instead of being defaulted, clamped or mapped to the nearest option
- **One Deadline per Call**: 30 seconds by default, covering admission, the key, every attempt, every backoff, the body and the decode. A retry never resets it
- **No Surprise Spending**: nothing is retried unless you ask, because a second evaluation can be billed again and need not give the same answer
- **Zero Third-Party Runtime Dependencies**: the core package references the framework and nothing else; `Microsoft.Extensions.*` lives in the separate dependency-injection package
- **Native AOT and Trimming**: `IsAotCompatible`, with reflection-based serialization disabled; a real consumer application is published with `PublishAot=true` in CI, because a library that merely builds proves nothing
- **A Contract You Can Review**: the wire types are generated offline from a committed contract extracted from TypeSafe's OpenAPI document, and CI regenerates them byte for byte
- **Safe to Share Across Tenants**: the key is asked for on every attempt and placed on that one request, never in `DefaultRequestHeaders`, and a call can bring its own credential
- **Privacy by Default**: no exception message, trace or metric carries the key, the state, your instructions or labels, or the answers, and a result prints only its size and the model that answered. Tests enforce those. One thing sits outside that boundary and is yours: a logging handler you add to the `HttpClient` sees everything — [SECURITY.md](SECURITY.md) has the detail

---

## Installation

```bash
# --prerelease, because every version so far is one and the CLI does not consider
# pre-release versions unless asked.
dotnet add package Kkdev92.Jev --prerelease

# Optional: IHttpClientFactory and IServiceCollection integration.
dotnet add package Kkdev92.Jev.DependencyInjection --prerelease
```

| Package | Purpose |
| --- | --- |
| `Kkdev92.Jev` | The client, decision plans, answers, errors, diagnostics |
| `Kkdev92.Jev.DependencyInjection` | `AddJev()`: a typed client over `IHttpClientFactory`, with the transport settings the client needs |

---

## Quick Start

```csharp
using Kkdev92.Jev;

// Build once, reuse for every message. The handles are how the answers are read back.
var builder = new JevDecisionPlanBuilder();

var department = builder.AddChoice<Department>(
    "department",
    "Which team should handle this message?",
    [
        new(Department.Billing, "billing", "Payments, invoices, refunds"),
        new(Department.Technical, "technical", "Bugs, outages, integrations"),
        new(Department.Sales, "sales", "Pricing, upgrades, new accounts"),
    ]);

var frustration = builder.AddScore("frustration", "How frustrated is the customer?", ["Calm", "Frustrated", "Very angry"]);
var urgent = builder.AddNoul("urgent", "Does the customer need an answer today?");

var plan = builder.Build();

// One long-lived HttpClient. No timeout of its own: the client's deadline is the one that applies.
using var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
{
    Timeout = Timeout.InfiniteTimeSpan,
};

var client = new JevClient(httpClient, new JevClientOptions
{
    Credential = StaticJevCredential.FromEnvironmentVariable(), // TYPESAFE_API_KEY, read now and only now
});

var result = await client.EvaluateAsync("I was charged twice this month and need a refund by Friday.", plan);

Department team = result.Get(department).Value;          // Department.Billing
double mood = result.Get(frustration).Value;              // an expectation over 0..2, such as 1.05
double today = result.Get(urgent).Probability;            // the probability of yes

enum Department { Billing, Technical, Sales }
```

With dependency injection:

```csharp
using Kkdev92.Jev.DependencyInjection;

services.AddJev((provider, options) =>
{
    options.Credential = provider.GetRequiredService<IJevCredential>(); // yours: a secret store, a vault
    options.Timeout = TimeSpan.FromSeconds(15);
});

// JevClient is then an ordinary typed client: inject it where it is needed.
```

No credential is looked for on its own. Resolving `JevClient` with no credential set fails
immediately, naming the option, and a host that validates options on start fails there instead.

A complete console application, with error handling, is in
[`src/samples/Triage.Console`](src/samples/Triage.Console/Program.cs).

---

## Why Kkdev92.Jev

The API itself is small — one evaluation operation and a model listing — so the interesting part is
not the transport. It is what happens around a call: a question has to reach the model exactly as
written, an answer has to be matched back to the question that asked it, and a decision made from
an answer has to be made from an answer the service actually gave. The choices this SDK makes are
about those three things:

- A question is validated and encoded once, when the plan is built, and every call reuses the bytes
- An answer is read through the handle its question returned, and a choice maps back to your own
  value
- A response missing an answer is refused, never filled in
- Nothing is retried by default, and connection failures are a separate opt-in
- One deadline covers the whole call, 30 s by default, and a retry never resets it
- When a retry is allowed, the wait the server asked for — `retry-after-ms`, or else `Retry-After` —
  is honoured in full, or the call ends; a retry is never sent early
- The API key comes only from a credential you pass; nothing is read implicitly
- An error message never carries the server's text, which can quote your input

It is written for a `net10.0` application that publishes under Native AOT and wants these failure
modes to be explicit. What it deliberately is not: a way to choose questions for you, a cache of
answers, a confidence threshold, or a promise about what the model will say. Those are the
application's.

---

## Usage

### Three kinds of question

```csharp
var builder = new JevDecisionPlanBuilder();

// Choice: one option from 1 to 255. The value can be anything: an enum, a string, your own struct.
var route = builder.AddChoice<Route>("route", "Where should this be queued?",
[
    new(new Route("standard", 3), "standard"),
    new(new Route("priority", 1), "priority", "Outages, security reports, legal deadlines"),
]);

// Score: 2 to 10 ordered levels, low to high. A level can be text, or structured JSON.
var severity = builder.AddScore("severity", "How severe is the reported problem?",
[
    "Cosmetic",
    JevContent.FromJson("""{"level":"degraded","means":"a workaround exists"}"""),
    JevContent.FromJson("""{"level":"down","means":"no workaround"}"""),
]);

// Noul: yes or no, answered with the probability of yes. Criteria are optional, and one-sided is fine.
var refund = builder.AddNoul("refund", "Is the customer asking for money back?",
    new JevNoulCriteria("A refund, chargeback or credit", "Anything else"));

var plan = builder.Build();
```

Ids, labels and text are sent exactly as given — never trimmed, normalised or re-cased — and
compared ordinally. A question id is only a key: TypeSafe's [HTTP reference](https://docs.typesafe.ai/api)
says it is not sent to the model and not used in inference. `default` instructions leave the field
out; `JevContent.Null` sends an explicit `null`.

### Three ways to send the state

```csharp
// Text. A string that looks like JSON is still text.
await client.EvaluateAsync("Our dashboard has been down since 9am.", plan);

// Your own type, through source-generated metadata. No reflection.
await client.EvaluateAsync(ticket, AppJsonContext.Default.Ticket, plan);

// Structured JSON you already have.
await client.EvaluateContentAsync(JevContent.FromJson(conversationJson), plan);
```

### Reading the answers

```csharp
var answer = result.Get(route);

answer.Value;                          // the Route you mapped "priority" to
answer.Label;                          // "priority"
answer.Confidence;                     // as the service reported it
answer.GetProbability("standard");     // every option has one
answer.Probabilities;                  // in option order, without allocating

var score = result.Get(severity);
score.Value;                           // the expectation over the levels, which can fall between them
score.Legend[2];                       // the level as the service echoed it, a JsonElement

result.ActualModel;                    // the version that answered, such as jev-1.13.0
result.Usage.InputTokens;              // what TypeSafe bills
result.Metadata.RequestId;             // quote this when asking TypeSafe about a call
```

A handle only reads a result of the plan it came from. Every question has exactly one answer, or
the call failed: there is no partial result to check for gaps.

### Per-call options

```csharp
await client.EvaluateAsync(text, plan, new JevRequestOptions
{
    Model = "jev-1.13.0",               // pin a version when results must stay comparable
    Timeout = TimeSpan.FromSeconds(5),
    AdditionalRetries = 2,              // 408, 429, 502, 503, 504 and 529 only
});
```

`jev-latest` is an alias that moves when TypeSafe ships a model. When confidence thresholds were
tuned on one version, pin it.

### Handle errors

```csharp
try
{
    var result = await client.EvaluateAsync(text, plan, cancellationToken: ct);
}
catch (JevHttpException ex) when (ex.IsRateLimited || ex.IsOverloaded)
{
    // RetryAfter is what the service asked for, when it said.
    await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(5), ct);
}
catch (JevHttpException ex) when (ex.StatusCode is 401 or 403)
{
    // The key was wrong or not allowed. ex.Message never contains it.
}
catch (JevProtocolException ex)
{
    // A 200 whose body did not answer the plan. ex.Error says how; the call was probably billed.
}
catch (JevTimeoutException ex)
{
    // The whole-call deadline passed. ex.Transmission says whether the request may have been processed.
}
```

Every `JevException` carries `Transmission` — whether the request was not sent, answered, or may
have been processed — because that is what decides whether trying again could spend twice. Each
exception type's XML documentation says exactly when it is thrown.

### Observe

The SDK publishes one `ActivitySource` and one `Meter`, both named `Kkdev92.Jev`
(`JevDiagnostics.ActivitySourceName` and `JevDiagnostics.MeterName`). Any tracing or metrics
pipeline subscribes to them by that name, and with nothing listening they cost next to nothing:

```csharp
using System.Diagnostics;

ActivitySource.AddActivityListener(new ActivityListener
{
    ShouldListenTo = source => source.Name == JevDiagnostics.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity => Console.WriteLine($"{activity.OperationName}: {activity.Duration}"),
});
```

One activity per call, and five instruments: duration, calls in flight, time queued, retries and
token usage. Tags are the operation, the outcome, the exception type, the status, sizes, attempts,
token counts and their type, and the request id — never content. The SDK writes no logs of its
own; with the DI package, `IHttpClientFactory` logs each request's method and URL, as it does for
every client it creates, and redacts header values unless told otherwise.

---

## What Is Guaranteed

- **An answer is the service's, or the call fails.** Nothing is renormalised, recomputed, clamped
  or defaulted. Probabilities that do not add up are reported as a warning, or refused in strict
  mode; a number outside its documented range by more than the numerical tolerance is refused in
  either
- **A handle reads only its own plan's answers.** Ownership is checked by reference, so a handle
  from another plan with the same question id cannot read the wrong answer
- **The deadline is the deadline.** A result is never returned after it, and a server-requested
  wait longer than what is left ends the call instead of holding it
- **Nothing is retried unless you ask**, and a connection failure — whose outcome is unknown — is
  retried only if you ask for that separately
- **Exception messages, traces and metrics carry no content.** Asserted by a test that plants
  markers in the key, the state, question ids, instructions, labels, levels, criteria, model names
  and the server's error text, and looks for them in every one of those outputs — an exception's
  `ToString()` included — after a success and after each kind of failure. A result prints its size
  and the model that answered; an answer and a plan print their size
- **The checked-in generated sources match the contract.** `codegen verify` regenerates and
  compares byte for byte on Linux and Windows
- **The public surface does not move by accident.** An approved API snapshot covers both
  packages; an addition, a removal or a changed signature shows up as a diff a reviewer has to
  accept
- **A published package contains only what it should.** Its entries, and those of its symbols
  package, are asserted against an allowlist, and every binary is read for build-machine paths

---

## Known Limitations

- **Rate limiting and overload have not been seen.** The bodies of `429` and `529`, and whether
  `Retry-After` or `retry-after-ms` accompany them, follow TypeSafe's documents rather than an
  observation. What a keyed call has settled is recorded in `spec/typesafe-v1/semantics.json`
- **The limits the SDK enforces are partly its own.** At most 255 choice options and 10 score
  levels are the HTTP reference's limits, which the OpenAPI document does not declare; at least two
  levels is the HTTP reference's advice, where the OpenAPI document requires one and the service
  accepts one. Past the limits the service answers `400`; the SDK refuses before sending. At least
  one option per choice, 1,024 questions and 1 MiB of encoded questions per plan are this SDK's. A
  request under every local limit can still exceed the model's context, which is counted in tokens
  the SDK cannot count
- **No streaming, no batching.** The API offers neither
- **One target.** `net10.0` only; no multi-targeting is planned
- **The consistency tolerance is provisional.** `1e-6` absolute and relative is this SDK's choice;
  TypeSafe's OpenAPI document says the probabilities sum to "approximately 1", and its HTTP
  reference that they sum to 1; neither gives a precision. The first keyed run saw probabilities
  with at most two decimals that summed to exactly 1, so nothing came near it. It stays provisional
  until more runs are in, and `Report` mode — the default — only warns
- **Exceptions cannot be constructed by callers.** To test your own error handling, answer with the
  status from a fake `HttpMessageHandler` — which also tests the mapping you depend on

---

## How It Works

```text
TypeSafe's OpenAPI document       network, human-initiated, never during a build
        v
extracted contract in spec/       structure only: no descriptions or examples
        v                         the document's SHA-256 recorded, the document not kept
commit and review                 the contract is a reviewable file
        v
offline generator                 allowlist, recorded conflicts, fail-closed on anything unknown
        v
generated wire types (internal)   18 files, compared byte for byte in CI
        v
handwritten runtime               plans, the response reader, deadline, retry, errors
```

The generated types are internal. The public surface — plans, handles, answers, errors — is
written by hand over them, so a change in TypeSafe's document can never reshape it on its own.

Where TypeSafe's own descriptions of the API differ — the OpenAPI document and the HTTP reference —
the difference is recorded in `spec/typesafe-v1/semantics.json` with its sources and the decision
taken, and the generator stops if a later contract no longer says what a decision relies on.

---

## Platform Requirements

|  |  |
| --- | --- |
| .NET | `net10.0` — single target, no multi-targeting |
| Language | C# 14; `LangVersion` is never `latest` or `preview` |
| Runtime dependencies | none in `Kkdev92.Jev`; `Microsoft.Extensions.Http` and `.Options` in `.DependencyInjection` only |
| Native AOT | supported and exercised in CI on a real consumer application |
| Trimming | `IsAotCompatible`; the trim and AOT analyzers report no warnings |
| SDK (to build) | exactly the version in `global.json`; `rollForward: disable` |
| TypeSafe API | OpenAPI `3.1.0`, `info.version` `0.2.0`, contract extracted 2026-09-22 |

The TypeSafe API version and this package's version are **independent axes**.

---

## Security and Privacy

- **No Telemetry**: the packages collect no usage data, and open no connection that a call of
  yours does not map to. They do make network requests — an API client is nothing else — but only
  these: an evaluation or a model listing, sent to the `BaseAddress` you configured. Nothing goes
  anywhere on a timer
- **The Key Only Goes Where You Point It**: `BaseAddress` must be an HTTPS origin, and the key is
  placed in one header of one request, never in `DefaultRequestHeaders`. The client the DI package
  builds does not follow redirects, so a key is never replayed to wherever a redirect points
- **No Implicit Key**: `Credential` is required, and
  `StaticJevCredential.FromEnvironmentVariable()` reads the variable only when you call it. Nothing
  is written to disk
- **Content Stays Out of Diagnostics**: exception messages, traces and metrics are built from
  values of the SDK's own — the operation, the outcome, the status, sizes, counts and the request
  id — and the service's machine-readable codes, and never from the key, the state, your
  instructions or labels, the answers, or the server's own error text, which can quote your input
- **Responses Are Untrusted Input**: a body is read under a byte limit and checked against the plan
  in full before any answer is exposed; what the contract does not allow is refused, never repaired
- **Bounded Reads and Waits**: request, response and error bodies each have a byte limit, and a
  server-requested wait longer than `MaximumServerRetryWait`, or than the time left, ends the call
  instead of holding it

Using this SDK does not by itself make an application compliant with TypeSafe's
[terms of use](https://typesafe.ai/legal/terms) or with any data protection regime. What is sent as
state and instructions, whether it may be sent at all, and how results are stored remain your
responsibility.

For vulnerability reporting, see [SECURITY.md](SECURITY.md).

---

## Documentation

|  |  |
| --- | --- |
| XML documentation | Every public type and member, in the package: what each call does, what it throws and when |
| [Changelog](CHANGELOG.md) | What changed in each release, and the TypeSafe contract it was generated from |
| [Contributing](CONTRIBUTING.md) | Building, testing, and the rules a change has to follow |
| [Security](SECURITY.md) | What is and is not recorded, and how to report a vulnerability |
| [Benchmarks](src/benchmarks/BASELINE.md) | What is measured, on what, and the numbers |
| [Where TypeSafe's descriptions differ](spec/typesafe-v1/semantics.json) | Each difference, its sources, and what this SDK does about it |

---

## Contributing

```bash
dotnet restore src/Jev.slnx
dotnet build   src/Jev.slnx -c Release
dotnet test --solution src/Jev.slnx -c Release -- --filter-not-trait Category=Package
```

Read [CONTRIBUTING.md](CONTRIBUTING.md) first — particularly the rules that are not negotiable,
and the note on never hand-editing anything under `Generated/` or the extracted contract.

Helpful things when reporting bugs:

- The package version, `dotnet --version`, and whether you publish with Native AOT
- The exception type, `ex.Message`, and `StatusCode`, `Error` or `Failure` as it applies — the
  message is built to be pasted
- `RequestId`, if the service sent one. It identifies the call to TypeSafe and nothing else
- Whether it fails while building the plan, while sending, or on the response

**Never paste an API key, a real state, or real instructions or labels into an issue.** A
reproduction with made-up text is always enough, and an issue is public and stays that way.

---

## Support & Maintenance Policy

This is a personal project maintained in spare time. It is active, but support is best-effort:
I'll do my best to review issues and PRs, and releases may be a bit slow sometimes — thank you for
your patience.

The `0.x` line is pre-release. Breaking changes are expected before `1.0.0` and are listed in the
[CHANGELOG](CHANGELOG.md). From `1.0.0` onward the public API follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Really appreciate you using it 💛

---

## License

The original code is [MIT](LICENSE).

What is taken from TypeSafe is kept to what an interoperating client has to use. The repository
holds the contract extracted from TypeSafe's OpenAPI document — its structure, with every
description, example and schema title removed — and the document's SHA-256, but not the document
itself, because TypeSafe's [terms of use](https://typesafe.ai/legal/terms) do not permit
redistributing it. [NOTICE](NOTICE), which ships inside every package, says what was taken from
where.

---

## Acknowledgments

- Built against the [TypeSafe API](https://docs.typesafe.ai/api); this is a third-party project,
  not affiliated with, endorsed by or sponsored by TypeSafe. "TypeSafe", "Jev" and "System One"
  are TypeSafe's names, used here only to identify the service this software interoperates with
