# Kkdev92.Jev.Extensions.AI

A Jev decision in front of any Microsoft.Extensions.AI `IChatClient`: a guard that judges a request
before it reaches the provider and the reply before it is returned, a router that sends each
request to the client a decision picks, and a decision a model can call as an `AIFunction`. Part of
[Kkdev92.Jev](https://www.nuget.org/packages/Kkdev92.Jev), an **unofficial** .NET 10 SDK for
[Jev, TypeSafe's System One API](https://docs.typesafe.ai/api).

> **Not affiliated with, endorsed by, or supported by TypeSafe.**
> "TypeSafe", "Jev" and "System One" are TypeSafe's names, used here only to identify the service
> this package interoperates with.

> **Pre-release.** Exercised offline, against a fake judge and a fake provider; it has not yet run
> in front of a real model. What has not been observed yet is listed under
> [known limitations](https://github.com/kkdev92/jev-dotnet/blob/main/README.md#known-limitations).

## Install

```bash
# --prerelease, because every version so far is one and the CLI does not consider pre-release
# versions unless asked.
dotnet add package Kkdev92.Jev.Extensions.AI --prerelease
```

## Guard a chat client

```csharp
using Kkdev92.Jev;
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
has been judged. A blocked reply has still been generated, so authorize tools where they run.

`JevRoutingChatClient<TRoute>` judges each request and sends it to the client registered for the
route the decision picks, to a fallback, or nowhere: `RequireReview()` throws, as the guard does.
`JevAIFunction.Create` exposes a decision as an `AIFunction` with one argument, `state`, the text to
judge, and your projection, serialized through the `JsonTypeInfo` and schema you give it, as the
result.

Each decision is one evaluation, billed like any other. The thresholds above are illustrations;
calibrate yours on your own data. What the judge sees, and how to change it, is in the
[readme](https://github.com/kkdev92/jev-dotnet/blob/main/README.md#with-microsoftextensionsai).

## Dependencies

`Kkdev92.Jev`, and `Microsoft.Extensions.AI` 11.x, which brings the 11.x `Microsoft.Extensions`
abstractions with it, on .NET 10 too.

## Learn more

| | |
|---|---|
| [Readme](https://github.com/kkdev92/jev-dotnet/blob/main/README.md) | Usage, what is guaranteed, known limitations, how it works |
| [Changelog](https://github.com/kkdev92/jev-dotnet/blob/main/CHANGELOG.md) | What changed, and against which contract |
| [Security](https://github.com/kkdev92/jev-dotnet/blob/main/SECURITY.md) | What is and is not recorded, and how to report a vulnerability |

Source, issues and discussion: <https://github.com/kkdev92/jev-dotnet>

## License

The original code is MIT, which is what the package metadata says. The packaged `NOTICE` file
states the package's relationship to TypeSafe; keep it with the package if you redistribute it.
