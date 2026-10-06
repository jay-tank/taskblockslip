namespace Taskblockslip;

/// <summary>Severity of a finding. "Blocker" fails the default run; "Warning" only fails with --strict.</summary>
public enum Severity
{
    Warning,
    Blocker,
}

/// <summary>One rule violation found while scanning for a blocking wait on a Task/Task&lt;T&gt;.</summary>
public sealed record Finding(
    string RuleId,
    Severity Severity,
    string FilePath,
    int Line,
    int Column,
    string MemberName,
    string Message);
