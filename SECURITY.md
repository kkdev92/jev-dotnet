# Security Policy

## Supported versions

Only the most recent release is supported. The `0.x` line is pre-release: breaking changes are
expected before `1.0.0`, and a fix ships in a new release rather than as a patch to an earlier one.
Both packages carry the same version and move together.

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
- A request sent to a host other than the configured origin, or a key sent over anything but HTTPS
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

## Security posture

This SDK carries an API key and whatever a caller chooses to send, and applies stricter defaults
than a general-purpose HTTP client.

**The client never looks for a key on its own.** `JevClientOptions.Credential` is required.
`StaticJevCredential.FromEnvironmentVariable()` reads an environment variable when *you* call it,
never implicitly, and trims it first, since a value read from a file or a shell often ends with a
newline. A key that is not printable ASCII without whitespace is refused before it is sent — a
stray line break, a typographic quote picked up in a paste, or an attempt to smuggle a header
line.

**The key goes in one header of one request.** It is attached to each request as it is built,
never to `HttpClient.DefaultRequestHeaders`, so a client shared between tenants cannot carry one
tenant's key into another's call.

**Only HTTPS, only the configured origin.** `BaseAddress` must be an absolute HTTPS origin with no
path, query, fragment or user information, and the validation message does not repeat it, because
a URI with user information carries a credential.

**Redirects are not followed by the client the DI package builds.** Its primary handler has
`AllowAutoRedirect = false`, so a key is never replayed to wherever a redirect points. A client you
construct yourself uses the `HttpClient` you give it; set the same on its handler.

**Every read is bounded.** A successful body is read under `MaxResponseBodyBytes`, an error body
under `MaxErrorBodyBytes`, and a request body is refused past `MaxRequestBodyBytes` before it is
sent.

**A server-requested wait is capped.** A wait the service asks for — `retry-after-ms`, or else
`Retry-After` — that is longer than `MaximumServerRetryWait`, or than what is left of the call's
deadline, ends the call instead of holding it, however large the number.

## Traces and HTTP instrumentation

This SDK's activities and metrics carry only the right-hand column above. .NET's own
`System.Net.Http` instrumentation records the request URL as well; for this API that is
`https://api.typesafe.ai/v1/systemone` or `/v1/models`, with no identifiers in it, and the key is
in a header that instrumentation does not record.

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
