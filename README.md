# taskblockslip

Static analysis for the classic .NET **"sync over async"** footgun: a direct
**`.Result`** property access, **`.Wait()`**/**`.Wait(timeout)`** method call,
or a **`.GetAwaiter().GetResult()`** chain, on an expression whose static type
is `Task`/`Task<T>` -- instead of `await`ing it. Roslyn syntax + a small,
self-contained per-file semantic check (`Microsoft.CodeAnalysis.CSharp`) --
it only parses the file(s) you pass it, it never builds, restores, or
executes the scanned project.

## Why

```csharp
// Blocks the calling thread until the Task completes. Compiles fine, works
// in a quick console test, then deadlocks the first time it runs on a thread
// with a captured SynchronizationContext.
var order = FetchOrderAsync(orderId).Result;

// The correct, non-blocking pattern.
var order = await FetchOrderAsync(orderId);
```

Blocking on async code this way can **deadlock** in any context with a
captured `SynchronizationContext` (classic ASP.NET, WPF/WinForms UI threads)
because the awaited continuation needs that same thread to resume -- and the
blocking call is holding it hostage. Even in ASP.NET Core (no
`SynchronizationContext`, so it usually survives without deadlocking) it
still causes real **thread-pool starvation** under load: every blocked
thread ties up a worker thread doing nothing instead of returning it to the
pool, which can cascade into request queuing and timeouts under real
traffic. This is one of the most well-known, continuously-relearned .NET
mistakes -- see [DETAILS.md](DETAILS.md) for the full writeup and an honest
prior-art comparison (Microsoft's own `Microsoft.VisualStudio.Threading.Analyzers`
ships a close relative of this exact check as `VSTHRD002` -- read that
section before assuming this is a novel find).

## Install

```bash
dotnet tool install --global Taskblockslip
```

**Not yet published to NuGet** -- this repository is the source; see
[docs/USAGE.md](docs/USAGE.md) for building and running it locally in the
meantime (`dotnet run --project src/Taskblockslip -- <path>`).

## Usage

```bash
taskblockslip Services/OrderService.cs   # scan a single file
taskblockslip .                          # scan a directory recursively for *.cs files
taskblockslip . --json                   # machine-readable output for CI
taskblockslip . --strict                 # also fail on warning-level findings
```

Exit codes: `0` clean, `1` a `TB001` blocker is present (or any finding under
`--strict`), `2` usage/path error.

## What it checks (v0.1)

| Rule | Severity | Meaning |
| --- | --- | --- |
| `TB001` | blocker | A direct `.Result` property access, `.Wait()`/`.Wait(timeout)` method call, or `.GetAwaiter().GetResult()` chain, on an expression whose static type resolves to `System.Threading.Tasks.Task`/`Task<TResult>`. |

Suppress a specific call with a `// taskblockslip: ignore` comment on its
statement line (leading or trailing).

## What it deliberately does not flag

- **`await someTask`** -- the correct, non-blocking pattern.
- **A same-named member on an unrelated type** -- e.g. a custom class with
  its own `.Result` property or `.Wait()` method that has nothing to do with
  `Task`. `taskblockslip` builds a small per-file semantic model and only
  flags a call that actually resolves to
  `System.Threading.Tasks.Task`/`Task<TResult>` -- this is exactly why
  semantic resolution matters here, not syntax-only name matching.
- **`ValueTask`/`ValueTask<T>`** -- out of scope by construction: unlike
  `Task`, `ValueTask` has no `.Result`/`.Wait()` blocking API to begin with
  (its `.Result`-equivalent access pattern is different and its
  `GetAwaiter().GetResult()` is a much rarer, differently-shaped call most
  teams don't hit the same way). See DETAILS.md.

A `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` call inside a test or
benchmark setup helper is **still in scope and still flagged** -- this tool
deliberately does not special-case test code, since blocking helpers in test
code are a real and common source of flaky/slow test suites too. Likewise,
a call site inside a provably synchronous top-level entry point with no
async alternative (e.g. a static field initializer) is still flagged --
see DETAILS.md's honest limitations section for why this tool can't fully
distinguish "avoidable" from "forced" sync-over-async without deeper
call-graph analysis.

See [DETAILS.md](DETAILS.md) for the full detection scope, the
semantic-model approach and its honest limits, and the prior-art
comparison; full CLI reference in [docs/USAGE.md](docs/USAGE.md).

## License

MIT
