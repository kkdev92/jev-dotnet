# Kkdev92.Jev.Extensions.AI.Evaluation

One Jev decision about a request and its reply, turned into as many Microsoft.Extensions.AI.Evaluation
metrics as you define. Part of [Kkdev92.Jev](https://www.nuget.org/packages/Kkdev92.Jev), an
**unofficial** .NET 10 SDK for [Jev, TypeSafe's System One API](https://docs.typesafe.ai/api).

> **Not affiliated with, endorsed by, or supported by TypeSafe.**
> "TypeSafe", "Jev" and "System One" are TypeSafe's names, used here only to identify the service
> this package interoperates with.

> **Pre-release.** Exercised offline, against a fake judge; it has not yet judged a real
> conversation. What has not been observed yet is listed under
> [known limitations](https://github.com/kkdev92/jev-dotnet/blob/main/README.md#known-limitations).

## Install

```bash
# --prerelease, because every version so far is one and the CLI does not consider pre-release
# versions unless asked.
dotnet add package Kkdev92.Jev.Extensions.AI.Evaluation --prerelease
```

## Evaluate a reply

```csharp
using Kkdev92.Jev;
using Kkdev92.Jev.Extensions.AI;
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

`JevEvaluator<T>` is an `IEvaluator`, so whatever runs evaluators takes it like any other. The
judge sees the conversation's text, as JSON: the last user message and the assistant's reply.
`JevMetric.String` makes a third kind of metric, and each kind can carry an interpretation.

A judge that fails throws by default. With `FailureBehavior` set to `ReturnFailedMetrics`, a failed
call to the judge — an HTTP error, a response that does not answer the plan, a lost connection, a
timeout, a full queue — returns every metric without a value, with an error diagnostic, instead.
`Metadata` copies the attempts, the duration and the token counts into each metric, and, only when
it includes `RequestIdentifiers`, the model names and the request id.

## Dependencies

`Kkdev92.Jev.Extensions.AI`, and `Microsoft.Extensions.AI.Evaluation` 11.x, which brings the 11.x
`Microsoft.Extensions` abstractions with it, on .NET 10 too.

## Learn more

| | |
|---|---|
| [Readme](https://github.com/kkdev92/jev-dotnet/blob/main/README.md) | Usage, what is guaranteed, known limitations, how it works |
| [Changelog](https://github.com/kkdev92/jev-dotnet/blob/main/CHANGELOG.md) | What changed, and against which contract |
| [Security](https://github.com/kkdev92/jev-dotnet/blob/main/PROJECT_GUIDE.md#security-and-private-reports) | What is and is not recorded, and how to report a vulnerability |

Source, issues and discussion: <https://github.com/kkdev92/jev-dotnet>

## License

The original code is MIT, which is what the package metadata says. The packaged `NOTICE` file
states the package's relationship to TypeSafe; keep it with the package if you redistribute it.
