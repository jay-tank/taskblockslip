using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Taskblockslip;

/// <summary>
/// Syntax-tree analysis, backed by a small, self-contained per-file semantic model (see below), for
/// the classic .NET "sync over async" footgun: a direct <c>.Result</c> property access,
/// <c>.Wait()</c>/<c>.Wait(timeout)</c> method call, or a <c>.GetAwaiter().GetResult()</c> chain, on
/// an expression whose static type is <c>Task</c>/<c>Task&lt;T&gt;</c> -- blocking the calling thread
/// synchronously on asynchronous work instead of <c>await</c>ing it.
///
/// Blocking on async code this way can deadlock in any context with a captured
/// <c>SynchronizationContext</c> (classic ASP.NET, WPF/WinForms UI threads) because the awaited
/// continuation needs that same thread to resume. Even in ASP.NET Core (no
/// <c>SynchronizationContext</c>, usually survives without deadlocking) it still causes real
/// thread-pool starvation under load: every blocked thread ties up a worker thread doing nothing
/// instead of returning it to the pool, which can cascade into request queuing/timeouts.
///
/// Like this workspace's <c>logmsgslip</c> package, this analyzer builds a small, self-contained
/// <see cref="CSharpCompilation"/> for each scanned file (that file's syntax tree plus the running
/// host's own referenced BCL assemblies) and uses its <see cref="SemanticModel"/> to confirm a
/// candidate <c>.Result</c>/<c>.Wait()</c>/<c>.GetAwaiter().GetResult()</c> genuinely resolves to
/// <c>System.Threading.Tasks.Task</c>/<c>Task&lt;TResult&gt;</c> -- not merely a member with a
/// matching name on an unrelated type. It still never builds, loads, or executes the SCANNED
/// project itself (no MSBuild, no project restore, no reference to the scanned project's own
/// dependencies) -- see DETAILS.md for exactly what this can and can't resolve.
/// </summary>
public static class Analyzer
{
    private const string SuppressionMarker = "taskblockslip: ignore";

    private static readonly Lazy<ImmutableArray<MetadataReference>> SharedReferences = new(BuildReferences);

    /// <summary>Analyzes a single in-memory source file.</summary>
    public static List<Finding> AnalyzeFile(string path, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var compilation = CSharpCompilation.Create(
            assemblyName: "TaskblockslipAnalysis",
            syntaxTrees: new[] { tree },
            references: SharedReferences.Value,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetCompilationUnitRoot();
        var findings = new List<Finding>();

        // GetAwaiter().GetResult() chains are matched off the outer GetResult() invocation; there is
        // no separate pass that inspects a bare .GetAwaiter() call on its own, so no deduplication
        // between the two is needed.
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            AnalyzeGetAwaiterGetResultChain(invocation, tree, model, findings);
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            AnalyzeWaitCall(invocation, tree, model, findings);
        }

        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            AnalyzeResultAccess(memberAccess, tree, model, findings);
        }

        return findings;
    }

    /// <summary>Analyzes several source files independently and concatenates their findings.</summary>
    public static List<Finding> AnalyzeFiles(IReadOnlyList<(string Path, string Source)> files)
    {
        var findings = new List<Finding>();
        foreach (var (path, source) in files)
        {
            findings.AddRange(AnalyzeFile(path, source));
        }

        return findings;
    }

    // ===================== TB001: .Result property access on a Task<T> =====================

    private static void AnalyzeResultAccess(
        MemberAccessExpressionSyntax memberAccess,
        SyntaxTree tree,
        SemanticModel model,
        List<Finding> findings)
    {
        if (memberAccess.Name.Identifier.Text != "Result")
        {
            return;
        }

        // A .Result access that is itself the target of an invocation (e.g. some kind of Result()
        // method call site shape) is not this property pattern; MemberAccessExpressionSyntax here
        // is already just the property-access node, so this guard only excludes it being consumed
        // as part of an invocation's Expression for a different member (defensive, should not occur
        // for a property).
        var symbolInfo = model.GetSymbolInfo(memberAccess);
        var propertySymbol = symbolInfo.Symbol as IPropertySymbol
            ?? symbolInfo.CandidateSymbols.OfType<IPropertySymbol>().FirstOrDefault();

        if (!IsTaskResultProperty(propertySymbol))
        {
            return;
        }

        if (HasSuppressionComment(memberAccess, tree))
        {
            return;
        }

        Report(
            findings,
            tree,
            memberAccess.GetLocation(),
            "Result",
            "'.Result' blocks the calling thread synchronously waiting for a Task<T> to complete " +
                "instead of 'await'ing it -- this can deadlock under a captured SynchronizationContext " +
                "(classic ASP.NET, WPF/WinForms) and causes thread-pool starvation under load even where " +
                "it doesn't deadlock (ASP.NET Core). Use 'await theTask' instead.");
    }

    private static bool IsTaskResultProperty(IPropertySymbol? symbol)
    {
        if (symbol is null || symbol.Name != "Result")
        {
            return false;
        }

        return IsTaskOfTType(symbol.ContainingType);
    }

    // ===================== TB001: .Wait() / .Wait(timeout) on a Task/Task<T> =====================

    private static void AnalyzeWaitCall(
        InvocationExpressionSyntax invocation,
        SyntaxTree tree,
        SemanticModel model,
        List<Finding> findings)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Name.Identifier.Text != "Wait")
        {
            return;
        }

        var symbolInfo = model.GetSymbolInfo(invocation);
        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (!IsTaskWaitMethod(methodSymbol))
        {
            return;
        }

        if (HasSuppressionComment(invocation, tree))
        {
            return;
        }

        var argDesc = invocation.ArgumentList.Arguments.Count > 0 ? "(timeout)" : "()";

        Report(
            findings,
            tree,
            invocation.GetLocation(),
            "Wait",
            $"'.Wait{argDesc}' blocks the calling thread synchronously waiting for a Task to complete " +
                "instead of 'await'ing it -- this can deadlock under a captured SynchronizationContext " +
                "(classic ASP.NET, WPF/WinForms) and causes thread-pool starvation under load even where " +
                "it doesn't deadlock (ASP.NET Core). Use 'await theTask' instead.");
    }

    private static bool IsTaskWaitMethod(IMethodSymbol? symbol)
    {
        if (symbol is null || symbol.Name != "Wait")
        {
            return false;
        }

        // Wait()/Wait(timeout) are declared on the non-generic System.Threading.Tasks.Task and
        // inherited by Task<TResult>; checking the containing type (walking the base-type chain in
        // IsSystemThreadingTasksTask) covers both a Task-typed and a Task<T>-typed receiver.
        return IsSystemThreadingTasksTaskOrBase(symbol.ContainingType);
    }

    // ===================== TB001: .GetAwaiter().GetResult() chain on a Task/Task<T> =====================

    private static void AnalyzeGetAwaiterGetResultChain(
        InvocationExpressionSyntax getResultInvocation,
        SyntaxTree tree,
        SemanticModel model,
        List<Finding> findings)
    {
        if (getResultInvocation.Expression is not MemberAccessExpressionSyntax getResultAccess ||
            getResultAccess.Name.Identifier.Text != "GetResult")
        {
            return;
        }

        if (getResultAccess.Expression is not InvocationExpressionSyntax getAwaiterInvocation ||
            getAwaiterInvocation.Expression is not MemberAccessExpressionSyntax getAwaiterAccess ||
            getAwaiterAccess.Name.Identifier.Text != "GetAwaiter")
        {
            return;
        }

        var getAwaiterSymbolInfo = model.GetSymbolInfo(getAwaiterInvocation);
        var getAwaiterMethod = getAwaiterSymbolInfo.Symbol as IMethodSymbol
            ?? getAwaiterSymbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (getAwaiterMethod is null ||
            getAwaiterMethod.Name != "GetAwaiter" ||
            !IsTaskOfTTypeOrNonGenericTask(getAwaiterMethod.ContainingType))
        {
            return;
        }

        var getResultSymbolInfo = model.GetSymbolInfo(getResultInvocation);
        var getResultMethod = getResultSymbolInfo.Symbol as IMethodSymbol
            ?? getResultSymbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (getResultMethod is null ||
            getResultMethod.Name != "GetResult" ||
            getResultMethod.ContainingType?.Name != "TaskAwaiter")
        {
            return;
        }

        if (HasSuppressionComment(getResultInvocation, tree))
        {
            return;
        }

        Report(
            findings,
            tree,
            getResultInvocation.GetLocation(),
            "GetAwaiter().GetResult()",
            "'.GetAwaiter().GetResult()' blocks the calling thread synchronously waiting for a Task " +
                "to complete instead of 'await'ing it -- this can deadlock under a captured " +
                "SynchronizationContext (classic ASP.NET, WPF/WinForms) and causes thread-pool " +
                "starvation under load even where it doesn't deadlock (ASP.NET Core). Use 'await " +
                "theTask' instead.");
    }

    // ===================== shared type checks =====================

    /// <summary>Is this type (or one it was constructed from) System.Threading.Tasks.Task&lt;TResult&gt;?</summary>
    private static bool IsTaskOfTType(INamedTypeSymbol? type)
    {
        if (type is null || !type.IsGenericType)
        {
            return false;
        }

        var definition = type.OriginalDefinition;
        return definition.Name == "Task" &&
            definition.Arity == 1 &&
            definition.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
    }

    /// <summary>Is this type exactly System.Threading.Tasks.Task (non-generic) or Task&lt;TResult&gt;?</summary>
    private static bool IsTaskOfTTypeOrNonGenericTask(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (IsTaskOfTType(type))
        {
            return true;
        }

        return !type.IsGenericType &&
            type.Name == "Task" &&
            type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
    }

    /// <summary>
    /// Is this type System.Threading.Tasks.Task itself, or (for a Task&lt;TResult&gt; receiver, whose
    /// inherited Wait() resolves with ContainingType still being the base Task) does its base-type
    /// chain reach System.Threading.Tasks.Task?
    /// </summary>
    private static bool IsSystemThreadingTasksTaskOrBase(INamedTypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (!t.IsGenericType &&
                t.Name == "Task" &&
                t.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
            {
                return true;
            }
        }

        return false;
    }

    // ===================== reporting / plumbing =====================

    private static void Report(
        List<Finding> findings,
        SyntaxTree tree,
        Location location,
        string memberName,
        string message)
    {
        var lineSpan = tree.GetLineSpan(location.SourceSpan);
        findings.Add(new Finding(
            "TB001",
            Severity.Blocker,
            FilePath: location.SourceTree?.FilePath ?? tree.FilePath,
            Line: lineSpan.StartLinePosition.Line + 1,
            Column: lineSpan.StartLinePosition.Character + 1,
            MemberName: memberName,
            Message: message));
    }

    /// <summary>
    /// Builds the metadata references for the small, self-contained per-file compilation: every
    /// assembly the running host process already has loaded/available (via the runtime's trusted
    /// platform assemblies list, which covers the BCL -- including System.Private.CoreLib, where
    /// Task/Task&lt;T&gt;/TaskAwaiter live). Computed once and reused across every file in a scan.
    /// </summary>
    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();

        void AddPath(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            if (seenPaths.Add(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpaList)
        {
            foreach (var p in tpaList.Split(Path.PathSeparator))
            {
                AddPath(p);
            }
        }

        // Belt-and-suspenders: make sure the assembly this tool itself actually needs (for Task) is
        // present even if the trusted-platform-assemblies list didn't include it for some hosting
        // reason.
        AddPath(typeof(object).Assembly.Location);
        AddPath(typeof(System.Threading.Tasks.Task).Assembly.Location);

        return refs.ToImmutableArray();
    }

    private static bool HasSuppressionComment(SyntaxNode node, SyntaxTree tree)
    {
        // Use the enclosing statement (not just the node itself) for both the leading-trivia check
        // and the line range: a node nested inside a larger statement (e.g. the ".Result" access in
        // "return DoAsync().Result;") doesn't carry the statement's own leading comment as ITS
        // leading trivia -- that comment attaches to the statement's first token ("return") instead.
        var statement = node.FirstAncestorOrSelf<StatementSyntax>() ?? node;
        var statementSpan = statement.GetLocation().SourceSpan;
        var lineSpan = tree.GetLineSpan(statementSpan);
        var startLine = lineSpan.StartLinePosition.Line;
        var endLine = lineSpan.EndLinePosition.Line;

        foreach (var trivia in statement.GetLeadingTrivia())
        {
            if (IsSuppressionTrivia(trivia))
            {
                return true;
            }
        }

        var text = tree.GetText();
        for (var line = startLine; line <= endLine && line < text.Lines.Count; line++)
        {
            if (text.Lines[line].ToString().Contains(SuppressionMarker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSuppressionTrivia(SyntaxTrivia trivia) =>
        (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) &&
        trivia.ToString().Contains(SuppressionMarker, StringComparison.Ordinal);
}
