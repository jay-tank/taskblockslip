# taskblockslip CLI reference

## Synopsis

```
taskblockslip <path> [<path> ...] [--json] [--strict]
taskblockslip -h | --help
```

## Arguments

- `<path>` -- one or more files or directories to scan. Directories are
  scanned recursively for `*.cs` files; `bin/` and `obj/` subfolders are
  skipped automatically. At least one path is required.

## Options

| Option | Effect |
| --- | --- |
| `--json` | Emit findings as a JSON array (`rule`, `severity`, `file`, `line`, `column`, `member`, `message`) instead of human-readable text. |
| `--strict` | Exit with code `1` if *any* finding is present, including warning-level findings (by default only a blocker-level finding causes a non-zero exit). |
| `-h`, `--help` | Print usage and exit `0`. |

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | No findings (or only warnings, without `--strict`). |
| `1` | At least one blocker finding, or (under `--strict`) at least one finding of any severity. |
| `2` | Usage error -- no paths given, or a given path does not exist. |

## Examples

```bash
# Scan an entire project directory
taskblockslip .

# Scan just the service layer
taskblockslip Services/ Handlers/

# CI-friendly: fail the build on any finding, machine-readable output
taskblockslip . --json --strict
```

## Suppressing a finding

Add a `// taskblockslip: ignore` comment on the blocking call's statement
line (leading or trailing):

```csharp
// taskblockslip: ignore -- static field initializer, no async alternative available
private static readonly Config Instance = LoadConfigAsync().Result;
```

```csharp
cache.WarmUpAsync().Wait(); // taskblockslip: ignore -- see ADR-031
```

## The safe pattern taskblockslip will never flag

```csharp
// Safe: await the task instead of blocking on it.
var order = await FetchOrderAsync(orderId);

// Safe: a same-named member on an unrelated type -- resolves to that
// type's own property/method, not System.Threading.Tasks.Task/Task<T>.
var outcome = somePollableOperation.Result;
somePollableOperation.Wait();
```

## Building and running from source (pre-publish)

```bash
dotnet build taskblockslip.slnx
dotnet run --project src/Taskblockslip -- examples/Bad.cs
dotnet test taskblockslip.slnx
```

## Rule reference

See [../DETAILS.md](../DETAILS.md) for the full rule definition, detection
scope, and honestly-documented limitations.
