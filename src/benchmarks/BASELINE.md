# Benchmark baseline

What the SDK costs per call on this side of the network, the measurements behind two of its design
choices, and how to run the numbers again.

## What this measures, and what it does not

The goal is **client-side overhead**: what the SDK itself spends to build a plan, write a request,
read a response, and run a whole call with the network taken out. It is not TypeSafe's latency,
which is orders of magnitude larger, and nothing here is a claim about how fast an evaluation is.
These numbers are a regression tripwire and the evidence for two design choices, not a ranking
against anything else.

Measurements fall into four tiers. Two are here:

| Tier | What | Here |
|---|---|---|
| P1: pure | Plan build, request encode, response decode — CPU and allocation | `PlanBenchmarks`, `RequestBenchmarks`, `DecodeBenchmarks` |
| P2: in-process | One whole `EvaluateAsync` over an in-process `HttpMessageHandler`: arguments, deadline, credential, body, headers, bounded read, decode | `CallBenchmarks` |
| P3: loopback HTTP | Sockets, TLS, HTTP/2, connection pooling, p95/p99 under concurrency | Not included |
| P4: live | The real service | Not automated: every call is billed |

## Environment

```text
date        2026-09-23
machine     Snapdragon X Elite (X1E80100), 12 cores, Windows 11 25H2, win-arm64 — a developer
            laptop on mains power, not an isolated runner
runtime     .NET 10.0.12, Arm64 RyuJIT; SDK 10.0.401
harness     the project in src/benchmarks: default job (not a short run), allocation measured
```

> Absolute times vary by 3–7% between launches of the same benchmarks; the ratios between the two
> decoders move by no more than two percentage points, and allocation is exact. Treat single-digit
> differences in absolute time as noise.

## The workloads

Fixed in advance, so that no workload is chosen because it is the one that wins. Every response is
internally consistent, so every path measured is the success path.

| Workload | Plan | Response |
|---|---|---|
| `triage` | One choice of 3, one score of 3 levels, one yes/no — the shape of TypeSafe's examples | 3 answers |
| `wide` | 16 choices of 8, 8 scores of 5 levels, 8 yes/no | 32 answers |
| `max-choice` | One choice of 255 options, the documented maximum | 255 probabilities |
| `many-noul` | 128 yes/no questions | 128 small answers |

## The adoption gate

A change made for performance is adopted only if it clears this gate on the main workloads, not on
a single winning case: about 5% less CPU, or about 20% less allocation with CPU no more than 3%
worse.

## Response decoding: why the reader is hand-written

The production reader against the reference decoder — the source-generated wire models, then a
mapping onto the plan with the same checks. The reference decoder stays as what the tests hold the
reader to.

| Workload | Reference decoder | Reader | Time | Allocated |
|---|---:|---:|---:|---:|
| `triage` | 3.10 µs, 5,976 B | 2.07 µs, 856 B | −33% | −86% |
| `wide` | 53.4 µs, 101,280 B | 37.3 µs, 9,464 B | −30% | −91% |
| `max-choice` | 49.8 µs, 93,272 B | 39.4 µs, 2,224 B | −21% | −98% |
| `many-noul` | 36.2 µs, 58,232 B | 22.9 µs, 3,232 B | −37% | −94% |

**The reader clears the gate on every workload**, and the ratios hold within two percentage points
on a repeat launch.

## Writing requests and reading responses

Three choices shape what a call allocates:

- The request is written into scratch borrowed from `ArrayPool` and copied out at its exact
  length. `Utf8JsonWriter` asks for three bytes per character before it writes a string, so an
  owned buffer would be grown, and grown again, for room it never uses. The scratch is cleared and
  returned before the first attempt; the body that is sent is never pooled.
- A response buffer starts one byte larger than `Content-Length`, so the read that finds the end of
  the body needs no second, larger buffer.
- Every writer uses an encoder that allows the whole Basic Multilingual Plane, still escaping
  HTML-sensitive characters, control characters, and everything beyond the plane, most emoji among
  them. The default encoder writes each character outside ASCII as a six-byte `\uXXXX`, which
  dominates the cost of a Japanese state.

Each table compares a build without these choices — growing buffers and the default encoder — with
the current one.

### A whole call (P2)

| Workload | Without | With | Time | Allocated |
|---|---:|---:|---:|---:|
| `triage` | 3.50 µs, 17.41 KB | 3.29 µs, 5.23 KB | −6% | −70% |
| `wide` | 39.7 µs, 42.31 KB | 40.1 µs, 27.93 KB | +1% | −34% |
| `max-choice` | 43.9 µs, 42.01 KB | 45.0 µs, 24.65 KB | +2.5% | −41% |
| `many-noul` | 26.5 µs, 29.35 KB | 26.0 µs, 19.31 KB | −2% | −34% |

**At least a third less allocated on every workload, and none more than 3% slower.** The time
differences are within the run-to-run variation noted above; the allocation is exact.

### Writing the request (P1)

The `triage` plan with a state of the given UTF-8 size.

| State | Without | With | Time | Allocated |
|---|---:|---:|---:|---:|
| 256 B, ASCII | 227 ns, 5.11 KB | 185 ns, 0.96 KB | −19% | −81% |
| 256 B, Japanese | 626 ns, 4.93 KB | 224 ns, 0.91 KB | −64% | −81% |
| 4 KiB, ASCII | 736 ns, 16.81 KB | 750 ns, 4.70 KB | +2% | −72% |
| 4 KiB, Japanese | 8.28 µs, 26.20 KB | 1.98 µs, 4.71 KB | −76% | −82% |
| 32 KiB, ASCII | 18.5 µs, 128.71 KB, Gen 2 | 4.87 µs, 32.67 KB | −74% | −75% |
| 32 KiB, Japanese | 92.7 µs, 205.15 KB, Gen 2 | 14.9 µs, 32.71 KB | −84% | −84% |

KB is 1,024 bytes.

**Without these choices, a 32 KiB state reaches the large-object heap.** It allocates past the
85,000-byte threshold and triggers generation-2 collections on every run; with them, no allocation
reaches it. Japanese and ASCII states of the same UTF-8 size allocate the same, because their
bodies are the same size: the escaped form is twice as long.

## Plan build (P1)

What building a plan costs, which a caller pays once per plan rather than per call.

| Workload | Time | Allocated |
|---|---:|---:|
| `triage` | 0.83 µs | 6.4 KB |
| `wide` | 15.2 µs | 72.5 KB |
| `max-choice` | 15.8 µs | 88.3 KB |
| `many-noul` | 13.5 µs | 45.1 KB |

## Running these

```bash
# one group, default job — what the numbers above used
dotnet run --project src/benchmarks/Kkdev92.Jev.Benchmarks -c Release -- --filter '*DecodeBenchmarks*'

# everything
dotnet run --project src/benchmarks/Kkdev92.Jev.Benchmarks -c Release -- --filter '*'
```

A change that claims to be faster comes with a before-and-after from the same machine, the same way,
and is held to the same gate. Benchmarks are deliberately not run in CI: they need a quiet machine
to mean anything, and a noisy number that fails a build teaches people to ignore the build.
