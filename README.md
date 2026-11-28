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

> **Status:** `0.3.0-alpha`. **Run against the live service with an API key:** on 2026-09-25 the
> live tests ([`Kkdev92.Jev.IntegrationTests`](src/tests/Kkdev92.Jev.IntegrationTests), run
> locally) passed — the three question kinds there were then, a structured state, structured
> levels, a question without instructions, the model list — and the `400`, `401`, `403` and `422`
> bodies the service sends are the ones the SDK reads. On 2026-11-09 the tests added since passed:
> long context, a state too long for the default context, images, and a box question. On
> 2026-11-24, after `0.3.0-alpha` was published, they all passed again on its source.
>
> Every call path is also exercised offline, against hand-written fakes of the HTTP API shaped after
> TypeSafe's own OpenAPI document. What no call has shown yet — a real `429` or `529`, a server
> other than TypeSafe's, the AI packages in front of a real model — is listed under
> [Known Limitations](#known-limitations).

---

## Table of Contents

Before reporting a problem or working on this project, read the [project guide](https://github.com/kkdev92/jev-dotnet/blob/HEAD/PROJECT_GUIDE.md).

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
- **Typed Answers**: a choice or a score level maps back to your own value — an enum, a string, a struct — through the handle its question returned, and a handle from another plan is refused rather than read
- **Strict Decoding**: a `200` is not accepted on faith. A missing, extra, duplicated or mistyped answer, a label nobody asked about, or a probability of 1.5 fails the call with `JevProtocolException` instead of being defaulted, clamped or mapped to the nearest option
- **One Deadline per Call**: 30 seconds by default, covering admission, the key, every attempt, every backoff, the body and the decode. A retry never resets it
- **No Surprise Spending**: nothing is retried unless you ask, because a second evaluation can be billed again and need not give the same answer
- **Many States, One Plan**: `EvaluateManyAsync` sends a list through a bounded number of workers and returns every outcome in input order — what succeeded, what failed and what never started
- **Your Own Server**: a server that serves the same API is reached at its own address, under a path prefix if it has one; plain HTTP and requests without a key are each an explicit opt-in
- **Microsoft.Extensions.AI**: optional packages put a Jev decision in front of any `IChatClient` — a guard, a router, a function a model can call — and turn the same decision into evaluation metrics
- **Zero Third-Party Runtime Dependencies**: the core package references the framework and nothing else; `Microsoft.Extensions.*` lives in the separate packages that integrate with it
- **Native AOT and Trimming**: `IsAotCompatible`, with reflection-based serialization disabled; consumer applications, one with the AI packages and one without, are published with `PublishAot=true` in CI and run, because a library that merely builds proves nothing
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

# Optional: a guard, a router and a function for Microsoft.Extensions.AI.
dotnet add package Kkdev92.Jev.Extensions.AI --prerelease

# Optional: Microsoft.Extensions.AI.Evaluation metrics from a decision.
dotnet add package Kkdev92.Jev.Extensions.AI.Evaluation --prerelease
```

| Package | Purpose |
| --- | --- |
| `Kkdev92.Jev` | The client, decision plans, answers, errors, diagnostics |
| `Kkdev92.Jev.DependencyInjection` | `AddJev()`: a typed client over `IHttpClientFactory`, with the transport settings the client needs, optionally read from configuration |
| `Kkdev92.Jev.Extensions.AI` | A guard and a router for any `IChatClient`, and a decision as an `AIFunction` |
| `Kkdev92.Jev.Extensions.AI.Evaluation` | `JevEvaluator<T>`: an `IEvaluator` whose metrics all come from one decision |

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
using var httpClient = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
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
compared ordinally. A question id is only a key: TypeSafe's
[page on questions](https://docs.typesafe.ai/primitives) says ids are for your code and are not
sent to the model. `default` instructions leave the field out; `JevContent.Null` sends an
explicit `null`.

An id can be left out where nothing outside the plan refers to it:
`builder.AddNoul("Does the customer need an answer today?")` names the question `q0`, the next one
left unnamed `q1`, and so on, skipping any id already taken. A generated id depends on the order
the questions were added in, so give one yourself to anything stored, compared across revisions of
a plan, or read by a person.

A fourth kind, the box question, finds where something is in an image. It is experimental, and
has [its own section](#images-and-box-questions-experimental).

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

result.ActualModel;                    // the version that answered, such as jev-1.14.0
result.Usage.InputTokens;              // what TypeSafe bills
result.Metadata.RequestId;             // quote this when asking TypeSafe about a call
```

A handle only reads a result of the plan it came from. Every question has exactly one answer, or
the call failed: there is no partial result to check for gaps.

### Scores in your own values

```csharp
var priority = builder.AddScore<int>("priority", "How soon does this need an answer?",
[
    new(10, "This week"),
    new(50, "Tomorrow"),
    new(90, "Today"),
]);

var answer = result.GetScore(priority);

answer.MostLikely;                     // 90: the most probable level, the first one listed on a tie
answer.Value;                          // the expectation over the levels' positions, 0 to 2, such as 1.82
answer.GetProbabilityOf(50);           // the probability of one level, looked up by its value
answer.GetLevel(0);                    // 10
```

The levels are sent in the order they are listed, low to high. `Value` stays on the scale of their
positions: it is not an average of your values, which need not be numbers at all.

### Per-call options

```csharp
await client.EvaluateAsync(text, plan, new JevRequestOptions
{
    Model = "jev-1.14.0",               // pin a version when results must stay comparable
    Timeout = TimeSpan.FromSeconds(5),
    AdditionalRetries = 2,              // 408, 429, 502, 503, 504 and 529 only
});
```

`jev-latest` is an alias that moves when TypeSafe ships a model. When confidence thresholds were
tuned on one version, pin it.

A request has to fit the model's context, which TypeSafe counts in tokens, per request and per
question; one that does not is refused with `400`, and `JevHttpException.ErrorType` is
`max_tokens_exceeded`. `UseLongContext = true` asks TypeSafe to raise both limits for that call.
TypeSafe documents long context as slower and less accurate than staying within the default
limits, so ask for it on the calls that need it; `JevClientOptions.UseLongContext` asks for it on
every evaluation. The limits are on TypeSafe's [models page](https://docs.typesafe.ai/models).

### Many states, one plan

```csharp
var batch = await client.EvaluateManyAsync(messages, plan, new JevBatchOptions
{
    MaxConcurrency = 4,                          // the default; the client's ConcurrencyLimit still applies
    FailureMode = JevBatchFailureMode.Collect,   // carry on past a failed item; the default stops
});

foreach (var item in batch.Items)                // one per input, in input order
{
    if (item.Result is { } result)
    {
        Route(item.Index, result.Get(department).Value);
    }
}
```

Every state is checked before the first is sent, and a list longer than `MaxItems`, 1,024 by
default, is refused. Each state is then one evaluation, under the client's deadline, retries and
admission like any other call, and billed as one. `EvaluateManyContentAsync` takes structured
states.

`FailFast`, the default, starts nothing new after a failure and throws `JevBatchException`, whose
`Snapshot` holds every outcome so far, the evaluations never started included. `Collect` records a
failed evaluation and carries on; anything that is not an SDK failure still stops the batch.
`OverallTimeout` and cancellation end a batch the same way, with `JevBatchTimeoutException` and
`JevBatchCanceledException`. Running a batch again sends again what had already succeeded, so read
the snapshot before replaying it. Each item's `Transmission` covers all of its attempts.

### Your own server

```csharp
var client = new JevClient(httpClient, new JevClientOptions
{
    BaseAddress = new Uri("http://127.0.0.1:8080/decision/"),  // evaluations go to /decision/v1/systemone
    AllowLoopbackHttp = true,                                    // plain HTTP, to this machine only
    AuthenticationMode = JevAuthenticationMode.None,             // no key, no Authorization header
    DefaultModel = "your-model",                                 // jev-latest is TypeSafe's name
});
```

A server that serves the same API is used by pointing `BaseAddress` at it. Its path is a prefix:
the API's own paths are appended to it, so a server behind a gateway path works as it is.

Plain HTTP has to be allowed, in one of two ways: `AllowLoopbackHttp` for `localhost`, `127.0.0.1`
and `[::1]`, or `AllowedInsecureHttpOrigins` for an exact origin, port included, such as
`http://inference.lan:8080`. Everything else stays HTTPS. A key sent over plain HTTP travels in
clear text, so a server on another machine that asks for one belongs behind HTTPS.

The address is checked as written, before `Uri` normalizes it, so what reaches the wire is what was
configured: user information, a query, a fragment, an empty port, a `.` or `..` segment and an
encoded slash are refused.

`JevAuthenticationMode.None` sends no key. No credential is asked for, setting one is refused, and
so is an `HttpClient` that already carries an `Authorization` header by default. A call can choose
its own mode with `JevRequestOptions.AuthenticationMode`.

Such a server's model list, where it has one, is in a format of its own, which `GetModelsAsync`
refuses like any response outside the contract; name the model in `DefaultModel` instead.

### Settings from configuration

```csharp
// "Jev": { "BaseAddress": "https://api.typesafe.ai", "Timeout": "00:00:15", "AdditionalRetries": 2 }
services.AddJev(configuration.GetSection("Jev"), (provider, options) =>
{
    options.Credential = provider.GetRequiredService<IJevCredential>();
});
```

The section is read when the options are built, and the delegate runs after it, so code wins over
configuration. The keys are the names of `JevClientOptions` properties, durations are written as
`TimeSpan` values, and `AllowedInsecureHttpOrigins` is a list. A key the client does not know, or a
value it cannot read, fails options validation without echoing what was written. There is no key
for a credential: it comes from code.

### Images and box questions (experimental)

TypeSafe marks images and box questions as experimental: the request format, the answers and the
limits can change between its releases, and so can the members of this SDK that use them —
`JevImage`, `AddImage`, `AddBox`, `BoxAnswer` and the overloads that take images. They are marked
`[Experimental("JEV001")]`, so using one is a compile error until you opt in:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);JEV001</NoWarn>
</PropertyGroup>
```

An image travels with the request, and you write its reference — a string the SDK makes —
wherever the image belongs; the model receives the image in its place:

```csharp
var photo = JevImage.FromBytes(File.ReadAllBytes("counter.jpg"));   // PNG or JPEG

var builder = new JevDecisionPlanBuilder();
var visible = builder.AddNoul("mug_visible", "Is there a coffee mug in `photo`?");
var mug = builder.AddBox("mug", "the coffee mug in `photo`");
var plan = builder.Build();

var state = JevContent.FromJson($$"""{"photo":"{{photo.Reference}}"}""");
var result = await client.EvaluateContentAsync(state, [photo], plan);

var box = result.Get(mug);   // Top, Bottom, Left, Right: fractions of the image from its top-left corner
```

A box answer is always four edges — there is no "not found" — so ask a noul beside it, as above,
and ignore the box when the noul says no. To crop or draw, multiply `Left` and `Right` by the
image's width and `Top` and `Bottom` by its height. Edges that cross are reported like any other
inconsistency, as `JevConsistencyCheck.BoxEdgeOrder`.

An image a question shows — a badge photo to match against, say — belongs to the plan instead:
write its reference in the question's instructions or criteria, add it with
`builder.AddImage(badge)`, and every call sends it.

A request with an image or a box question is sent as `multipart/form-data`, with the header
`TypeSafe-Danger-Zone-Images: true`. Before anything is sent, the SDK refuses images and references
that do not match — an image no text references, a reference whose image is not sent — and a box
question with no image to look at. Images count towards `JevClientOptions.MaxRequestBodyBytes`,
4 MiB by default, which applies to the whole form; raise it for larger images. The sizes TypeSafe
accepts are on its [images page](https://docs.typesafe.ai/images).

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

### With Microsoft.Extensions.AI

```csharp
using Kkdev92.Jev.Extensions.AI;
using Microsoft.Extensions.AI;

var questions = new JevDecisionPlanBuilder();
var abusive = questions.AddNoul("abusive", "Is the message abusive or harassing?");

var screen = new JevGuardPolicy(new JevProjection<JevGuardDecision>(questions.Build(), result =>
    result.Get(abusive).Probability switch
    {
        >= 0.9 => JevGuardDecision.Block,
        >= 0.5 => JevGuardDecision.Review,
        _ => JevGuardDecision.Allow,
    }));

// The judge is a JevClient of its own; provider is any IChatClient.
IChatClient chat = new ChatClientBuilder(provider)
    .UseJevGuard(judge, inputPolicy: screen)
    .Build();
```

An input policy is decided before the provider is called. `Allow` passes the request on, `Review`
throws `JevReviewRequiredException`, and `Block` throws `JevGuardRejectedException`, or answers
with `BlockedMessage` when `BlockBehavior` is `ReturnMessage`. An output policy decides on the
reply before it is returned. Streaming under an output policy is refused unless
`OutputStreamingMode` is `Buffer`, which holds the reply, within byte and update limits, until it
has been judged. A blocked reply has still been generated: a decision afterwards cannot undo what a
tool did meanwhile, so authorize tools where they run.

`JevRoutingChatClient<TRoute>` judges each request and sends it to the `IChatClient` registered for
the route the decision picks, to a fallback, or nowhere: `RequireReview()` throws, as the guard
does. `JevAIFunction.Create` exposes a decision as an `AIFunction` a model can call, with one
argument, `state`, the text to judge, and your projection, serialized through the `JsonTypeInfo`
and schema you give it, as the result.

`JevEvaluator<T>`, in `Kkdev92.Jev.Extensions.AI.Evaluation`, turns one decision about a request and
its reply into as many named metrics as you define:

```csharp
using Kkdev92.Jev.Extensions.AI.Evaluation;

var review = new JevDecisionPlanBuilder();
var answered = review.AddNoul("answered", "Does the reply answer what the customer asked?");

var evaluator = new JevEvaluator<double>(judge,
    new JevProjection<double>(review.Build(), result => result.Get(answered).Probability),
    [
        JevMetric.Numeric<double>("answered", p => p),
        JevMetric.Boolean<double>("needs_follow_up", p => p < 0.5),
    ]);

var evaluation = await evaluator.EvaluateAsync(messages, reply);   // one evaluation, two metrics
```

What the judge sees is the conversation's text, as JSON: the last user message for an input policy
or a router, the assistant's reply for an output policy, and both for the evaluator.
`JevChatStateOptions` widens or narrows that, or replaces it with a projection of your own, and
content other than text is refused unless it is told to skip it. A decision is made on the messages
it is given, so stateful requests — a continuation token, background responses, and for a router a
conversation id — are refused.

Each decision is one evaluation, billed like any other: a request through a guard with both
policies is judged twice. `TotalTimeout` puts one deadline over all of it, the provider included,
and ends it with `JevPipelineTimeoutException`. The thresholds above are illustrations; calibrate
yours on your own data.

A pipeline with every piece — an input guard, a router, a buffered output guard, a function and an
evaluator, all against fakes — is in
[`src/tests/Kkdev92.Jev.AiAotSmokeTests`](src/tests/Kkdev92.Jev.AiAotSmokeTests), which CI publishes
with Native AOT and runs. [`src/samples/Evaluation.Reporting`](src/samples/Evaluation.Reporting/Program.cs)
stores an evaluation with Microsoft.Extensions.AI.Evaluation.Reporting, which the packages do not
reference.

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
- **A batch accounts for every input.** Each one ends succeeded, failed, canceled or not started, in
  input order, and an exception that ends a batch carries that snapshot
- **Nothing is retried unless you ask**, and a connection failure — whose outcome is unknown — is
  retried only if you ask for that separately
- **Exception messages, traces and metrics carry no content.** Asserted by a test that plants
  markers in the key, the state, question ids, instructions, labels, levels, criteria, model names
  and the server's error text, and looks for them in every one of those outputs — an exception's
  `ToString()` included — after a success and after each kind of failure. A result prints its size
  and the model that answered; an answer and a plan print their size
- **The checked-in generated sources match the contract.** `codegen verify` regenerates and
  compares byte for byte on Linux and Windows
- **The public surface does not move by accident.** An approved API snapshot covers every
  package; an addition, a removal or a changed signature shows up as a diff a reviewer has to
  accept
- **A published package contains only what it should.** Its entries, and those of its symbols
  package, are asserted against an allowlist, and every binary is read for build-machine paths

---

## Known Limitations

- **Rate limiting and overload have not been seen.** The bodies of `429` and `529`, and whether
  `Retry-After` or `retry-after-ms` accompany them, follow TypeSafe's documents rather than an
  observation. What a keyed call has settled is recorded in `spec/typesafe-v1/semantics.json`
- **The limits the SDK enforces are partly its own.** At most 255 choice options and 10 score
  levels, and at least one option per choice, are what TypeSafe's OpenAPI document and HTTP
  reference declare. At least two levels is the advice of its page on scores, where those two
  documents require one and the service accepts one. Past the limits the service answers `400`;
  the SDK refuses before sending. 1,024 questions and 1 MiB of encoded questions per plan are this
  SDK's. A request under every local limit can still exceed the model's context, which is counted
  in tokens the SDK cannot count
- **Images and box questions are experimental, at TypeSafe and here.** Keyed calls have shown the
  service accepting the form as this SDK writes it — a PNG in the state, a PNG in a question's
  instructions beside one in the state, and a box question — and answering in the documented
  shape. JPEG has been checked offline only, and the base64 form of an image is not used. The SDK
  checks an image's format and its references, not its size: the pixel and byte limits on
  TypeSafe's images page can change between its releases, so they are left to the service, which
  refuses an image outside them
- **That long context raises the limits is TypeSafe's word.** A keyed evaluation that asks for it
  is accepted, and a state too long for the default context, sent without it, is refused with `400`
  and `max_tokens_exceeded`; no keyed call has yet sent a state that long with long context on
- **No streaming, and batching is the SDK's.** The API offers neither. `EvaluateManyAsync` sends one
  evaluation per state, each counted and billed as one
- **Other servers of the API are checked offline only.** The tests decode response examples
  published for two of them, and no model on such a server has answered a call of this SDK. Their
  model lists come in another format, which `GetModelsAsync` refuses, and structured state, long
  context, images and box questions sent to them are untried
- **The AI packages have only met fakes.** The guard, the router, the function and the evaluator are
  exercised offline, with a fake judge and a fake provider; none has run in front of a real model
- **One target.** `net10.0` only; no multi-targeting is planned
- **The consistency tolerance is provisional, and tighter than the service's rounding.** `1e-6`
  absolute and relative is this SDK's choice. TypeSafe's OpenAPI document and HTTP reference say
  the probabilities sum to "approximately 1", and its page on scores that they sum to 1; none gives
  a precision. The keyed runs have seen probabilities with at most two decimals, and distributions
  that summed to exactly 1. A score can still sit off the probability-weighted mean of the
  probabilities beside it: on 2026-11-24, `jev-1.14.0` answered a three-level score 0.01 away from
  that mean, and the page on scores itself shows the calculation as approximate, to two decimals.
  `Report` mode, the default, returns such an answer with a `ScoreIsExpectation` warning; `Strict`
  refuses the whole response unless `NumericalAbsoluteTolerance` covers the gap. The tolerance
  stays provisional until more runs are in
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
generated wire types (internal)   23 files, compared byte for byte in CI
        v
handwritten runtime               plans, the response reader, deadline, retry, errors
```

The generated types are internal. The public surface — plans, handles, answers, errors — is
written by hand over them, so a change in TypeSafe's document can never reshape it on its own.

Where TypeSafe's own descriptions of the API differ — the OpenAPI document, the HTTP reference and
the other documentation pages — the difference is recorded in `spec/typesafe-v1/semantics.json`
with its sources and the decision taken, and the generator stops if a later contract no longer says
what a decision relies on.

---

## Platform Requirements

|  |  |
| --- | --- |
| .NET | `net10.0` — single target, no multi-targeting |
| Language | C# 14; `LangVersion` is never `latest` or `preview` |
| Runtime dependencies | none in `Kkdev92.Jev`; `Microsoft.Extensions.Http`, `.Options` and `.Configuration.Abstractions` 10.x in `.DependencyInjection`; `Microsoft.Extensions.AI` 11.x in `.Extensions.AI`; `Microsoft.Extensions.AI.Evaluation` 11.x in `.Extensions.AI.Evaluation` |
| Native AOT | supported, and exercised in CI on consumer applications with and without the AI packages |
| Trimming | `IsAotCompatible`; the trim and AOT analyzers report no warnings |
| SDK (to build) | exactly the version in `global.json`; `rollForward: disable` |
| TypeSafe API | OpenAPI `3.1.0`, `info.version` `0.2.0`, contract extracted 2026-10-24 |

The TypeSafe API version and this package's version are **independent axes**.

`Microsoft.Extensions.AI` 11 depends on the 11.x `Microsoft.Extensions` abstractions, so an
application that references the AI packages resolves those, on .NET 10 too. Without them, nothing
here asks for more than 10.x.

---

## Security and Privacy

- **No Telemetry**: the packages collect no usage data, and open no connection that a call of
  yours does not map to. They do make network requests — an API client is nothing else — but only
  these: an evaluation or a model listing, sent to the `BaseAddress` you configured. Nothing goes
  anywhere on a timer
- **The Key Only Goes Where You Point It**: `BaseAddress` is HTTPS unless you allow plain HTTP — to
  this machine, or to an origin you name — and the key is placed in one header of one request,
  never in `DefaultRequestHeaders`. The client the DI package builds follows no redirects and keeps
  no cookies, so a key is never replayed to wherever a redirect points
- **No Implicit Key**: `Credential` is required unless you choose to send none, with
  `JevAuthenticationMode.None`, and `StaticJevCredential.FromEnvironmentVariable()` reads the
  variable only when you call it. Nothing is written to disk
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
| [Project guide](https://github.com/kkdev92/jev-dotnet/blob/HEAD/PROJECT_GUIDE.md#development-instructions) | Building, testing, and the rules a change has to follow |
| [Security](https://github.com/kkdev92/jev-dotnet/blob/HEAD/PROJECT_GUIDE.md#security-and-private-reports) | What is and is not recorded, and how to report a vulnerability |
| [Benchmarks](src/benchmarks/BASELINE.md) | What is measured, on what, and the numbers |
| [Where TypeSafe's descriptions differ](spec/typesafe-v1/semantics.json) | Each difference, its sources, and what this SDK does about it |

---

## Contributing

Start with the [project guide](https://github.com/kkdev92/jev-dotnet/blob/HEAD/PROJECT_GUIDE.md) for development and verification. To submit an issue or pull request, see the [shared contribution guidelines](https://github.com/kkdev92/.github/blob/main/CONTRIBUTING.md).

---

## Support & Maintenance Policy

For a bug report, follow the [reporting instructions](https://github.com/kkdev92/jev-dotnet/blob/HEAD/PROJECT_GUIDE.md#reporting-a-problem).

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
