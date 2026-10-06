using Taskblockslip;

var paths = new List<string>();
bool json = false;
bool strict = false;

foreach (var arg in args)
{
    switch (arg)
    {
        case "--json":
            json = true;
            break;
        case "--strict":
            strict = true;
            break;
        case "-h":
        case "--help":
            PrintHelp();
            return 0;
        default:
            paths.Add(arg);
            break;
    }
}

if (paths.Count == 0)
{
    PrintHelp();
    return 2;
}

var files = new List<string>();
foreach (var p in paths)
{
    if (Directory.Exists(p))
    {
        files.AddRange(Directory.EnumerateFiles(p, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));
    }
    else if (File.Exists(p))
    {
        files.Add(p);
    }
    else
    {
        Console.Error.WriteLine($"taskblockslip: path not found: {p}");
        return 2;
    }
}

var sources = new List<(string Path, string Source)>();
foreach (var file in files.Distinct().OrderBy(f => f, StringComparer.Ordinal))
{
    try
    {
        sources.Add((file, File.ReadAllText(file)));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"taskblockslip: could not read {file}: {ex.Message}");
        return 2;
    }
}

var allFindings = Analyzer.AnalyzeFiles(sources);
allFindings = allFindings.OrderBy(f => f.FilePath, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();

if (json)
{
    Reporter.PrintJson(allFindings, Console.Out);
}
else
{
    Reporter.PrintText(allFindings, Console.Out);
}

bool hasBlocker = allFindings.Any(f => f.Severity == Severity.Blocker);
bool hasAnyUnderStrict = strict && allFindings.Count > 0;
return hasBlocker || hasAnyUnderStrict ? 1 : 0;

static void PrintHelp()
{
    Console.WriteLine("""
        taskblockslip -- static analysis for the classic .NET "sync over async"
        footgun: a direct '.Result' property access, '.Wait()'/'.Wait(timeout)'
        method call, or a '.GetAwaiter().GetResult()' chain, on an expression
        whose static type is Task/Task<T>, instead of 'await'ing it. Blocking
        on async code this way can deadlock in any context with a captured
        SynchronizationContext (classic ASP.NET, WPF/WinForms UI threads) and,
        even where it doesn't deadlock (ASP.NET Core), it still causes real
        thread-pool starvation under load.

        Usage:
          taskblockslip <path> [<path> ...] [--json] [--strict]

        Options:
          --json     machine-readable output
          --strict   also fail (exit 1) on warning-level findings
          -h, --help show this message

        Exit codes: 0 clean, 1 findings present (blocker, or any finding under --strict), 2 usage error.
        """);
}
