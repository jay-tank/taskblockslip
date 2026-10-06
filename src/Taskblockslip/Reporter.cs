using System.Text.Json;

namespace Taskblockslip;

public static class Reporter
{
    public static void PrintText(IReadOnlyList<Finding> findings, TextWriter writer)
    {
        if (findings.Count == 0)
        {
            writer.WriteLine("taskblockslip: no findings.");
            return;
        }

        foreach (var f in findings)
        {
            var tag = f.Severity == Severity.Blocker ? "blocker" : "warning";
            writer.WriteLine($"{f.FilePath}:{f.Line}:{f.Column}: [{f.RuleId} {tag}] {f.Message}");
        }

        var blockers = findings.Count(f => f.Severity == Severity.Blocker);
        var warnings = findings.Count(f => f.Severity == Severity.Warning);
        writer.WriteLine();
        writer.WriteLine($"taskblockslip: {findings.Count} finding(s) -- {blockers} blocker(s), {warnings} warning(s).");
    }

    public static void PrintJson(IReadOnlyList<Finding> findings, TextWriter writer)
    {
        var payload = findings.Select(f => new
        {
            rule = f.RuleId,
            severity = f.Severity.ToString().ToLowerInvariant(),
            file = f.FilePath,
            line = f.Line,
            column = f.Column,
            member = f.MemberName,
            message = f.Message,
        });

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        writer.WriteLine(json);
    }
}
