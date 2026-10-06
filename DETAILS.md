# Design notes

## The gap

`Task`/`Task<T>` expose three ways to synchronously block the calling thread
until the asynchronous work they represent finishes:

```csharp
var order = FetchOrderAsync(orderId).Result;               // Task<T>.Result
FetchOrderAsync(orderId).Wait();                            // Task.Wait()
var order = FetchOrderAsync(orderId).GetAwaiter().GetResult(); // chain
```

All three compile without error, all three "work" in a quick manual test,
and all three are a well-known, continuously-relearned .NET footgun known
as **"sync over async."** The correct pattern is to `await` the task instead
and let the rest of the call chain be `async` all the way up:

```csharp
var order = await FetchOrderAsync(orderId);
```

## Why this is dangerous, not just "a style nit"

- **Deadlock risk under a captured `SynchronizationContext`.** Classic
  ASP.NET (System.Web, pre-Core) and WPF/WinForms UI threads each install a
  `SynchronizationContext` that marshals a `Task`'s continuation back onto
  the original thread by default. If that original thread is the one
  blocked on `.Result`/`.Wait()`/`.GetAwaiter().GetResult()`, the
  continuation can never run on it -- and the blocking call can never
  return, because it's waiting for that continuation to signal completion.
  Classic deadlock, and one of the single most asked "why does my async
  code hang" questions in .NET's history.
- **Thread-pool starvation even where it doesn't deadlock.** ASP.NET Core
  has no `SynchronizationContext` by default, so the classic deadlock
  usually doesn't happen there. But the blocking call still ties up
  whichever thread-pool thread is running it for the full duration of the
  awaited work, instead of returning that thread to the pool to serve
  another request while the I/O/network/etc. operation is in flight. Under
  real traffic, every such blocked thread is one fewer available worker --
  which can cascade into request queuing and timeouts as load increases,
  even though nothing ever technically "hangs forever."

## Verified prior art (checked before building)

This gap is **not** undiscovered territory, and it's important to be
upfront about that:

- **`Microsoft.VisualStudio.Threading.Analyzers`' `VSTHRD002`** ("Avoid
  problematic synchronous waits") is close to this exact check: it flags
  `.Wait()`, `.GetAwaiter().GetResult()`, and `.Result` access on
  `Task`/`ValueTask`/awaiters, at **Critical** default severity, and ships
  with an automated code fix that can refactor the blocking call chain into
  an `async` one. Confirmed directly against the rule's own documentation
  page (`microsoft.github.io/vs-threading/analyzers/VSTHRD002.html`) at the
  time of writing.
  - **Why `taskblockslip` still has a place despite `VSTHRD002`:**
    - **Requires adding a broader analyzer package to the project.**
      `VSTHRD002` is one rule inside `Microsoft.VisualStudio.Threading.Analyzers`,
      which is normally pulled in as an MSBuild-integrated analyzer
      `PackageReference` on the *scanned* project itself -- it runs during
      `dotnet build`/live IDE analysis as part of that project's own
      dependency graph. `taskblockslip` is a standalone CLI that parses
      `.cs` files directly with no project reference, no restore, and no
      change to the scanned project's own `.csproj` at all -- useful for a
      snippet, a partial checkout, a code-review diff, or a CI job that
      wants a fast, dependency-free lint pass without adding VS Threading's
      broader analyzer surface (which ships many other rules beyond this
      one, e.g. around `JoinableTaskFactory` usage) as a project dependency.
    - **A separately enforced exit code, independent of `.editorconfig`
      severity discipline.** Like this workspace's other packages,
      `taskblockslip` is a deliberate, opt-in CLI check with its own exit
      code wired into CI, rather than depending on a team having already
      promoted an analyzer's severity in `.editorconfig`.
  - **CA1849** ("Call async methods when in an async method") is a related
    but narrower Roslyn built-in rule: it also calls out `Task.Wait()`,
    `Task<T>.Result`, and `Task.GetAwaiter().GetResult()`, but **only within
    a method that is already `async`** (confirmed against the current
    Microsoft Learn "CA1849" rule page: "All methods where an Async-suffixed
    equivalent exists will produce this warning when called from a
    Task-returning method"), and it is **disabled by default** even in
    .NET 10. `taskblockslip` flags the same call shape **regardless of
    whether the enclosing method is itself `async`** -- a huge share of
    real-world sync-over-async bugs are specifically in *non-async* methods
    (constructors, synchronous interface implementations, static
    initializers) calling into async code, which CA1849 does not cover at
    all by its own stated scope.
  - **Meziantou.Analyzer's `MA0042`/`MA0045`** are a third-party analyzer
    package covering a similar area (blocking calls in async contexts) --
    confirmed via general web search, not independently re-verified against
    its live docs page; mentioned here for completeness rather than as a
    load-bearing claim.
  - **Honest bottom line**: the underlying rule -- "don't block on a
    `Task` synchronously" -- is well-trodden ground with real, mature
    tooling (`VSTHRD002` especially). `taskblockslip`'s honest value
    proposition is the same as this workspace's other packages: a
    standalone, zero-MSBuild-integration, no-build, CI-friendly,
    exit-code-driven CLI surface for a check most ASP.NET/WPF/WinForms
    projects would benefit from but haven't necessarily wired up via a
    broader analyzer dependency -- not the discovery of a previously-uncaught
    bug class.
- **This workspace's other `dotnet/` packages**
  ([`logmsgslip`](../logmsgslip/), [`sqlrawslip`](../sqlrawslip/),
  [`optionslip`](../optionslip/), [`scopeslip`](../scopeslip/),
  [`tokendrop`](../tokendrop/), [`gatelapse`](../gatelapse/),
  [`loopvet`](../loopvet/), [`kernelward`](../kernelward/),
  [`cachewary`](../cachewary/), [`sockettap`](../sockettap/),
  [`streamslip`](../streamslip/)) check unrelated shapes -- `ILogger`
  message templates, EF Core raw-SQL string building, Options-pattern DI
  lifetimes, `DbContext` lifetime mismatches, cancellation-token
  propagation, auth-attribute coverage, hosted-service shutdown loops, LLM
  call-site token limits, caching gaps, socket handling, and untrusted
  file-name-derived paths. None inspect blocking waits on `Task`. Confirmed
  via `grep -rli "getawaiter\|\.result\b\|sync.over.async" packages/dotnet/*/README.md
  packages/dotnet/*/DETAILS.md` across this workspace's `dotnet/` catalog
  before building: no existing package in this workspace does this check.

## Verified current versions/API (checked, not assumed)

- **.NET 10** is the current LTS release; `taskblockslip` targets `net10.0`,
  matching this workspace's other `dotnet/` packages.
- **`Microsoft.CodeAnalysis.CSharp` 5.9.0** is referenced directly, matching
  the version already verified current for this workspace's `logmsgslip`
  package.
- **`VSTHRD002`'s scope, default (Critical) severity, and package
  (`Microsoft.VisualStudio.Threading.Analyzers`)** were confirmed against
  the rule's own current documentation page rather than assumed.
- **`CA1849`'s scope (async-method-only) and disabled-by-default status in
  .NET 10** were confirmed against the current Microsoft Learn "CA1849" rule
  page rather than assumed.

## What `taskblockslip` actually checks

For every candidate syntax shape in a scanned file (`Analyzer.cs`):

1. **`.Result` property access** -- any `MemberAccessExpressionSyntax` named
   `Result`. **Semantic check**: the resolved property symbol's
   `ContainingType` must be the generic `System.Threading.Tasks.Task<TResult>`
   (checked via `OriginalDefinition`, so any closed generic instantiation
   like `Task<Order>` or `Task<int[]>` matches). A same-named `Result`
   property on an unrelated, user-defined type does **not** match, because
   it resolves to that type's own property, not `Task<TResult>`'s.
2. **`.Wait()`/`.Wait(timeout)` method call** -- any
   `InvocationExpressionSyntax` whose callee member name is `Wait`.
   **Semantic check**: the resolved method symbol's `ContainingType` (or one
   of its base types, since `Wait()` is declared on the non-generic `Task`
   and inherited by `Task<TResult>`) must resolve to
   `System.Threading.Tasks.Task`. A same-named `Wait()` method on an
   unrelated type does **not** match.
3. **`.GetAwaiter().GetResult()` chain** -- an `InvocationExpressionSyntax`
   calling `GetResult()` whose receiver is itself an
   `InvocationExpressionSyntax` calling `GetAwaiter()`. **Semantic check**:
   the resolved `GetAwaiter()` method's `ContainingType` must be
   `System.Threading.Tasks.Task` or `Task<TResult>`, **and** the resolved
   `GetResult()` method's `ContainingType` must be named `TaskAwaiter` (the
   BCL's `System.Runtime.CompilerServices.TaskAwaiter`/`TaskAwaiter<TResult>`).
   Both ends of the chain must resolve for the finding to fire -- a
   same-shaped `GetAwaiter()`/`GetResult()` pair on an unrelated custom
   awaitable type does **not** match.
4. Each matched site is reported as **`TB001`** (blocker), with the
   specific variant (`.Result`/`.Wait()`/`.Wait(timeout)`/
   `.GetAwaiter().GetResult()`) distinguished in the message text, plus fix
   guidance to `await` instead.
5. A `// taskblockslip: ignore` comment on the call's enclosing statement
   (leading or trailing) suppresses that one finding.

## Why semantic resolution (unlike this workspace's syntax-only scanners)

Several other `dotnet/` packages in this workspace (`sqlrawslip`,
`optionslip`, `scopeslip`, etc.) are deliberately pure-syntax, matching
method names on any receiver with no compilation at all. `taskblockslip`
uses a small per-file semantic model instead -- like `logmsgslip` -- because
`.Result`/`.Wait()`/`.GetAwaiter()`/`.GetResult()` are exactly the kind of
short, generic member names a custom type is likely to define for its own
unrelated purpose (a polling/operation-result wrapper with its own `.Result`
or `.Wait()`, for instance). A name-only match would risk real false
positives on exactly that shape; the worked test case
`ResultOnUnrelatedTypeWithSameMemberName_NoFindings` (and its `.Wait()` and
`.GetAwaiter().GetResult()` counterparts) in this package's test suite
exists specifically to prove the semantic check does its job. This is still
not a full build: no MSBuild invocation, no project restore, no reference
to the *scanned* project's own dependencies -- only the running host's own
BCL assemblies (which is where `Task`/`Task<T>`/`TaskAwaiter` themselves
live, so no extra package reference is needed the way `logmsgslip` needed
`Microsoft.Extensions.Logging.Abstractions`).

## Honest limitations

- **Per-file, no-global-usings compilation.** Each file is compiled in
  isolation with only that file's own `using` directives -- this tool does
  not see the scanned project's `.csproj`, so
  `<ImplicitUsings>enable</ImplicitUsings>`-style implicit global usings are
  NOT available. In practice this matters less here than for `logmsgslip`,
  because `Task`/`Task<T>` themselves resolve from the BCL regardless of
  file-level `using` directives -- the receiver expression's own type still
  needs to be inferable within the single-file compilation, though (e.g. a
  method return type declared in another file not included in the scan may
  not resolve, which conservatively suppresses a finding rather than
  producing a false positive).
- **Deliberately does not special-case test/benchmark code.** A
  `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` call inside what looks
  like a test setup helper or benchmark fixture is still flagged, on
  purpose: blocking helpers in test code are a real, common source of
  flaky and slow test suites, not a harmless convenience, so there is no
  attempt to detect "this looks like test code" and suppress accordingly.
- **Cannot distinguish "avoidable" sync-over-async from "genuinely forced"
  sync-over-async without deeper call-graph analysis.** A small number of
  call sites are legitimately hard or impossible to make fully async --
  a `static` field initializer (which cannot itself be `async`), or some
  older interop/COM-callback scenarios that must present a synchronous
  method signature to a caller outside the analyzed code. `taskblockslip`
  still flags these: distinguishing "there is truly no async path all the
  way up the call chain" from "there is one, but nobody has refactored to
  use it yet" requires whole-program call-graph analysis this single-file,
  no-build tool does not attempt. This is a real, honest limitation, not a
  hypothetical edge case -- teams adopting this tool should expect to
  suppress a small number of genuinely-forced sites with
  `// taskblockslip: ignore` rather than treating every finding as
  automatically fixable.
- **`ValueTask`/`ValueTask<T>` are out of scope by construction, not by
  omission.** Unlike `Task`, `ValueTask`/`ValueTask<T>` do not expose a
  `.Result`/`.Wait()` blocking API in the same shape at all (there is no
  `ValueTask.Wait()`, and `ValueTask<T>.Result` has sufficiently different
  usage norms -- it's far more commonly accessed only after confirming
  `IsCompletedSuccessfully`, which is itself a recognized safe pattern) --
  so there is no equivalent blocking footgun shaped the same way to check
  for here. `ValueTask`'s own `.GetAwaiter().GetResult()` exists but is a
  much rarer call shape in practice; it is not covered by this version.
- **`VSTHRD002` substantially overlaps this check** on any project willing
  to add `Microsoft.VisualStudio.Threading.Analyzers` as a project
  dependency (see "Verified prior art" above) -- `taskblockslip`'s distinct
  value is being a standalone, no-build, zero-MSBuild-integration CLI with
  its own enforced exit code, not a previously uncaught bug class.
