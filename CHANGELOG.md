# Changelog

All notable changes to this project are documented in this file.

From 1.0.0 onward this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Pre-1.0 releases follow it in spirit; their breaking changes are marked **Breaking**.

The TypeSafe API version and the package version are **independent axes**. A new version of
TypeSafe's contract does not by itself cause a major bump: what decides the package version is
what changes for a caller of the package.

Each release records the contract it was generated from — the OpenAPI `info.version` and the
SHA-256 of the document TypeSafe served — because that is what actually determines the wire.

Dates are UTC, taken from when the packages went to nuget.org.

## [Unreleased]

## [0.1.1-alpha] - 2026-09-25

### Changed

- `JevHttpException.Message` names `api_usage_error`, the error type TypeSafe sends with a `400`
  for a request it will not serve, such as an unknown model. The server's own message, which names
  the model, still stays out.

### Notes

- **Verified against the live service with an API key.** The live tests pass, and the `400`,
  `401`, `403` and `422` bodies the service sends are the ones the SDK reads. A question without
  `instructions` is accepted, structured score levels come back in the legend exactly as sent, and
  over the documented limits the service answers `400`. `429` and `529` remain unobserved.

## [0.1.0-alpha] - 2026-09-23

First public build. Generated from TypeSafe's OpenAPI document `3.1.0`, `info.version` `0.2.0`,
retrieved 2026-09-22 (document SHA-256 `a191f8a7…c0360d5`, contract SHA-256
`167e85e6…91d852f6`).

### Added

- `JevClient`: `EvaluateAsync` for text state, `EvaluateAsync<TState>` for a typed state through
  source-generated metadata, `EvaluateContentAsync` for structured JSON, and `GetModelsAsync`.
- `JevDecisionPlanBuilder` and `JevDecisionPlan`: questions are validated and encoded once, when
  the plan is built, and every call copies the encoded bytes into its request. `AddChoice<T>` maps
  each option to a value of any type; `AddScore` takes 2 to 10 levels of text or JSON; `AddNoul`
  takes optional, one-sided criteria.
- Typed handles (`JevChoiceHandle<T>`, `JevScoreHandle`, `JevNoulHandle`) and non-allocating answer
  views (`ChoiceAnswer<T>`, `ScoreAnswer`, `NoulAnswer`). A handle is checked by reference against
  the plan a result answers, so a handle from another plan is refused rather than read.
- `JevContent`, which keeps text, JSON, an explicit `null` and "left out" apart, because the
  contract does.
- Requests keep text outside ASCII as UTF-8 instead of six-byte escapes, which halves what Japanese
  text adds to a body. HTML-sensitive characters, control characters, and everything beyond the
  Basic Multilingual Plane — most emoji among them — are still escaped.
- A response reader that checks the whole contract before any answer is exposed: every question
  answered exactly once, nothing answered that was not asked, types, labels and level keys matched
  exactly, numbers finite and within their documented range to the numerical tolerance, token
  counts exact integers, no known field repeated in any spelling, and no field name that does not
  decode — unknown fields' names included, though not the members inside their values, which are
  skipped unread. It is held to a second, independent decoder over fixed cases and over generated
  responses.
- Consistency checks — a distribution sums to one, a choice is the most probable option, a score is
  the expectation of its levels — reported as `JevConsistencyWarning`, or refused under
  `JevNumericalConsistency.Strict`. Nothing is ever renormalised or recomputed.
- One deadline per call (`Timeout`, 30 s by default) covering admission, the credential, every
  attempt, every backoff, the body and the decode.
- Opt-in retries (`AdditionalRetries`, 0 by default) of `408`, `429`, `502`, `503`, `504` and
  `529`, with full-jitter backoff. The wait the service asks for — `retry-after-ms`, or else
  `Retry-After` — is honoured or declined, never shortened, however large the number. Connection
  failures are retried only with `RetryNetworkFailures`.
- An optional concurrency limit with a bounded queue (`ConcurrencyLimit`, `MaxQueuedRequests`).
- Exceptions that say how far a request got (`JevRequestTransmission`), with messages that carry
  no content: `JevHttpException`, `JevProtocolException`, `JevTransportException`,
  `JevLimitException`, and `JevTimeoutException`, which derives from `TimeoutException`.
- An `ActivitySource` and a `Meter`, both named `Kkdev92.Jev`, with a fixed allowlist of tags.
- `Kkdev92.Jev.DependencyInjection`: `AddJev()` registers a typed client over
  `IHttpClientFactory` with an infinite `HttpClient.Timeout`, a primary handler that does not
  follow redirects, options validated on start, and one admission gate shared by every client the
  container creates.

### Notes

- Targets `net10.0` only.
- Nothing is retried unless asked. An evaluation can be billed again and need not give the same
  answer, so a client that silently re-sends one is a liability.
- **Not yet verified against the live service with a key.** Every call path is exercised offline,
  against hand-written fakes of the HTTP API shaped after TypeSafe's OpenAPI document. The live
  endpoint has been checked only without a key: the `401` and `403` bodies, the response headers
  and HTTP/2. What only a keyed call can settle is listed under Known Limitations in the README.

[Unreleased]: https://github.com/kkdev92/jev-dotnet/compare/v0.1.1-alpha...HEAD
[0.1.1-alpha]: https://github.com/kkdev92/jev-dotnet/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/kkdev92/jev-dotnet/releases/tag/v0.1.0-alpha
