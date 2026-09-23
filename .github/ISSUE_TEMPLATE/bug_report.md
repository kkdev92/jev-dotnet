---
name: Bug Report
about: Report a bug to help improve Kkdev92.Jev
title: '[Bug] '
labels: bug
assignees: ''
---

> **Do not paste an API key, real state text, or real instructions, labels or question ids.**
> Those are yours and can say more than you think; a reproduction with made-up text is always
> enough. An issue is public and stays that way — anything posted here should be assumed to be
> permanent.

## Environment

- **Package and version**: (e.g., Kkdev92.Jev 0.1.0-alpha)
- **.NET SDK**: output of `dotnet --version`
- **OS**: (e.g., Windows 11, Ubuntu 24.04)
- **Native AOT / trimming**: (yes / no)
- **Model**: `jev-latest`, or the versioned id you pinned

## Description

A clear description of the bug.

## Steps to Reproduce

1.
2.
3.

## Expected Behavior

What you expected to happen.

## Actual Behavior

What actually happened.

## Code Example

```csharp
// Minimal code to reproduce the issue, with made-up text
```

## Error Messages

The exception type, `ex.Message`, and whichever of `StatusCode`, `ErrorType`, `Error` or
`Failure` applies. The SDK builds its messages from values of its own and keeps your content and
the server's text out of them, which is why the message is the part to paste.

`RequestId`, if the service sent one, identifies the call to TypeSafe and nothing else.

Do not paste a captured body (`ErrorBody`, `RawResponseBody`): it contains the service's view of
your request.

## Additional Context

Any other relevant information.
