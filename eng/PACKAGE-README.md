# Kkdev92.Jev

An **unofficial** .NET 10 SDK for [Jev, TypeSafe's System One API](https://docs.typesafe.ai/api).
Questions are declared once as a typed decision plan, validated and encoded once, and reused on
every call; answers come back through typed handles, after the response has been checked against
the contract.

> **Not affiliated with, endorsed by, or supported by TypeSafe.**
> "TypeSafe", "Jev" and "System One" are TypeSafe's names, used here only to identify the service
> this package interoperates with.

> **Pre-release, and not yet run against the live service with an API key.** Every call path is
> exercised offline, against hand-written fakes of the HTTP API shaped after TypeSafe's OpenAPI
> document, and the live endpoint has been checked only without a key. A few behaviours only a
> keyed call can settle are listed under
> [known limitations](https://github.com/kkdev92/jev-dotnet/blob/main/README.md#known-limitations).

## Why this exists

- `net10.0` and C# 14, targeted directly
- **Zero non-framework runtime dependencies** in the core package
- Questions encoded once per plan, not once per call; a choice maps back to your own value type
- A response that does not answer the plan — a missing or extra answer, an unknown label, a
  probability of 1.5 — fails loudly instead of being defaulted or clamped
- One deadline per call, no retries unless you ask, and a wait the server asks for never cut
  short
- Native AOT and trimming treated as a requirement: a consumer application is published with
  `PublishAot=true` in CI

## Install

```bash
# --prerelease, because every version so far is one and the CLI does not consider pre-release
# versions unless asked.
dotnet add package Kkdev92.Jev --prerelease
dotnet add package Kkdev92.Jev.DependencyInjection --prerelease   # optional
```

This readme ships in both packages.

| Package | Purpose |
|---|---|
| `Kkdev92.Jev` | The client, decision plans, answers, errors, diagnostics |
| `Kkdev92.Jev.DependencyInjection` | `AddJev()`: a typed client over `IHttpClientFactory` |

## Getting started

```csharp
using Kkdev92.Jev;

var builder = new JevDecisionPlanBuilder();

var department = builder.AddChoice<Department>(
    "department",
    "Which team should handle this message?",
    [
        new(Department.Billing, "billing", "Payments, invoices, refunds"),
        new(Department.Technical, "technical", "Bugs, outages, integrations"),
    ]);

var frustration = builder.AddScore("frustration", "How frustrated is the customer?", ["Calm", "Frustrated", "Very angry"]);
var urgent = builder.AddNoul("urgent", "Does the customer need an answer today?");

var plan = builder.Build(); // once; reuse it for every call

using var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
{
    Timeout = Timeout.InfiniteTimeSpan, // the client's own deadline applies instead
};

var client = new JevClient(httpClient, new JevClientOptions
{
    Credential = StaticJevCredential.FromEnvironmentVariable(), // TYPESAFE_API_KEY, read only because you asked
});

var result = await client.EvaluateAsync("I was charged twice this month and need a refund.", plan);

Department team = result.Get(department).Value;
double mood = result.Get(frustration).Value;     // an expectation over the levels 0..2
double today = result.Get(urgent).Probability;   // the probability of yes

enum Department { Billing, Technical }
```

With `Kkdev92.Jev.DependencyInjection`:

```csharp
using Kkdev92.Jev.DependencyInjection;

services.AddJev(options => options.Credential = myCredential);
```

Errors are typed: `JevHttpException` (with `StatusCode`, `RetryAfter`, `RequestId`),
`JevProtocolException` for a `200` that does not answer the plan, `JevTransportException`,
`JevLimitException`, and `JevTimeoutException`, which derives from `TimeoutException`. Each says
whether the request may already have been processed — and billed.

## Privacy

What a caller sends — the state, instructions, labels, question ids — and the answers that come
back can say more than they seem to. No exception message, trace or metric this SDK produces
carries any of them, the key, or the server's own error text, which can quote your input; and
printing a result prints its size and the model that answered, never an answer.

One thing sits outside that boundary and is yours to handle: a logging `DelegatingHandler` added to
the client's pipeline sees the `Authorization` header and the request body in full.

`JevClient` never looks for a key on its own — `Credential` is required — and nothing here writes
one to disk. The key goes over HTTPS only, on the one request it belongs to, never in
`DefaultRequestHeaders`, and every body is read under a byte limit.

Using this SDK does not by itself make an application compliant with TypeSafe's
[terms of use](https://typesafe.ai/legal/terms) or with any data protection regime. What is sent as
state and instructions, whether it may be sent at all, and how results are stored remain the
consuming application's responsibility.

## Documentation

Every public type and member carries XML documentation, which this package includes: what each
call does, and what it throws and when.

| | |
|---|---|
| [Readme](https://github.com/kkdev92/jev-dotnet/blob/main/README.md) | Usage, what is guaranteed, known limitations, how it works |
| [Changelog](https://github.com/kkdev92/jev-dotnet/blob/main/CHANGELOG.md) | What changed, and against which contract |
| [Security](https://github.com/kkdev92/jev-dotnet/blob/main/SECURITY.md) | What is and is not recorded, and how to report a vulnerability |

Source, issues and discussion: <https://github.com/kkdev92/jev-dotnet>

Author and other projects: <https://kkdev92.dev/>

## License

The original code is MIT, which is what the package metadata says.

The package contains no text from TypeSafe's documentation. The wire contract it implements — the
paths, JSON member names and value types any client of the service has to use — is taken from
TypeSafe's OpenAPI document, and reaches the package only as names and constants in compiled code.
The packaged `NOTICE` file says so, and states the package's relationship to TypeSafe; keep it with
the package if you redistribute it.
