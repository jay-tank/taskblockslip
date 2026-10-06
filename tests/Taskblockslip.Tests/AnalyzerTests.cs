using Taskblockslip;

namespace Taskblockslip.Tests;

public class AnalyzerTests
{
    private const string Preamble = "using System.Threading.Tasks;\n\n";

    // ===================== TB001: .Result on a Task<T>-returning method call =====================

    [Fact]
    public void ResultOnTaskOfT_FlagsTB001()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                int M() => DoAsync().Result;

                Task<int> DoAsync() => Task.FromResult(1);
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.Severity == Severity.Blocker && f.MemberName == "Result");
    }

    // ===================== TB001: .Wait() on a Task-returning call =====================

    [Fact]
    public void WaitOnTask_FlagsTB001()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                void M() => DoAsync().Wait();

                Task DoAsync() => Task.CompletedTask;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "Wait");
    }

    [Fact]
    public void WaitWithTimeoutOnTask_FlagsTB001()
    {
        const string src = """
            using System;
            using System.Threading.Tasks;

            class C
            {
                void M() => DoAsync().Wait(TimeSpan.FromSeconds(1));

                Task DoAsync() => Task.CompletedTask;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "Wait");
    }

    // ===================== TB001: .GetAwaiter().GetResult() chain =====================

    [Fact]
    public void GetAwaiterGetResultOnTaskOfT_FlagsTB001()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                int M() => DoAsync().GetAwaiter().GetResult();

                Task<int> DoAsync() => Task.FromResult(1);
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "GetAwaiter().GetResult()");
    }

    [Fact]
    public void GetAwaiterGetResultOnNonGenericTask_FlagsTB001()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                void M() => DoAsync().GetAwaiter().GetResult();

                Task DoAsync() => Task.CompletedTask;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "GetAwaiter().GetResult()");
    }

    // ===================== correct pattern: await =====================

    [Fact]
    public void AwaitSomeTask_NoFindings()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                async Task<int> M() => await DoAsync();

                Task<int> DoAsync() => Task.FromResult(1);
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Empty(findings);
    }

    [Fact]
    public void AwaitNonGenericTask_NoFindings()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                async Task M() => await DoAsync();

                Task DoAsync() => Task.CompletedTask;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Empty(findings);
    }

    // ===================== semantic scope: must genuinely resolve to Task/Task<T> =====================

    [Fact]
    public void ResultOnUnrelatedTypeWithSameMemberName_NoFindings()
    {
        // A custom class with its own unrelated "Result" property must not be confused with the
        // real System.Threading.Tasks.Task<T>.Result.
        const string src = """
            class NotATask
            {
                public string Result => "done";
            }

            class C
            {
                string M()
                {
                    var thing = new NotATask();
                    return thing.Result;
                }
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Empty(findings);
    }

    [Fact]
    public void WaitOnUnrelatedTypeWithSameMemberName_NoFindings()
    {
        // A custom class with its own unrelated "Wait()" method must not be confused with the real
        // System.Threading.Tasks.Task.Wait().
        const string src = """
            class NotATask
            {
                public void Wait() { }
            }

            class C
            {
                void M()
                {
                    var thing = new NotATask();
                    thing.Wait();
                }
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Empty(findings);
    }

    [Fact]
    public void GetAwaiterGetResultOnUnrelatedType_NoFindings()
    {
        // A custom awaitable-shaped type that is not Task/Task<T> at all.
        const string src = """
            class NotATaskAwaiter
            {
                public string GetResult() => "done";
            }

            class NotATask
            {
                public NotATaskAwaiter GetAwaiter() => new NotATaskAwaiter();
            }

            class C
            {
                string M()
                {
                    var thing = new NotATask();
                    return thing.GetAwaiter().GetResult();
                }
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Empty(findings);
    }

    // ===================== Task.WhenAll(...).Result =====================

    [Fact]
    public void TaskWhenAllResult_FlagsTB001()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                int[] M(Task<int>[] tasks) => Task.WhenAll(tasks).Result;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "Result");
    }

    // ===================== Task.Delay(...).Wait() =====================

    [Fact]
    public void TaskDelayWait_FlagsTB001()
    {
        const string src = """
            using System;
            using System.Threading.Tasks;

            class C
            {
                void M() => Task.Delay(TimeSpan.FromSeconds(1)).Wait();
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "Wait");
    }

    // ===================== nested inside lambda / local function =====================

    [Fact]
    public void ResultInsideLambda_FlagsTB001()
    {
        const string src = """
            using System;
            using System.Threading.Tasks;

            class C
            {
                void M()
                {
                    Func<int> work = () => DoAsync().Result;
                    work();
                }

                Task<int> DoAsync() => Task.FromResult(1);
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "Result");
    }

    [Fact]
    public void WaitInsideLocalFunction_FlagsTB001()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                void M()
                {
                    void Inner() => DoAsync().Wait();
                    Inner();
                }

                Task DoAsync() => Task.CompletedTask;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "Wait");
    }

    // ===================== fully-qualified System.Threading.Tasks.Task variant =====================

    [Fact]
    public void FullyQualifiedTaskOfT_ResultStillFlagsTB001()
    {
        const string src = """
            class C
            {
                int M()
                {
                    System.Threading.Tasks.Task<int> task = DoAsync();
                    return task.Result;
                }

                System.Threading.Tasks.Task<int> DoAsync() => System.Threading.Tasks.Task.FromResult(1);
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "Result");
    }

    [Fact]
    public void FullyQualifiedTask_WaitStillFlagsTB001()
    {
        const string src = """
            class C
            {
                void M()
                {
                    System.Threading.Tasks.Task task = DoAsync();
                    task.Wait();
                }

                System.Threading.Tasks.Task DoAsync() => System.Threading.Tasks.Task.CompletedTask;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Contains(findings, f => f.RuleId == "TB001" && f.MemberName == "Wait");
    }

    // ===================== suppression =====================

    [Fact]
    public void LeadingSuppressionComment_SkipsFinding()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                int M()
                {
                    // taskblockslip: ignore -- reviewed, static field initializer, no async alternative
                    return DoAsync().Result;
                }

                Task<int> DoAsync() => Task.FromResult(1);
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Empty(findings);
    }

    [Fact]
    public void TrailingSuppressionComment_SkipsFinding()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                void M() => DoAsync().Wait(); // taskblockslip: ignore -- see ADR-042

                Task DoAsync() => Task.CompletedTask;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Empty(findings);
    }

    // ===================== multiple calls / files =====================

    [Fact]
    public void MultipleBlockingCallsInOneFile_FlagsEachSeparately()
    {
        const string src = """
            using System.Threading.Tasks;

            class C
            {
                void M()
                {
                    var a = DoAsync().Result;
                    DoVoidAsync().Wait();
                    var b = DoAsync().GetAwaiter().GetResult();
                }

                Task<int> DoAsync() => Task.FromResult(1);
                Task DoVoidAsync() => Task.CompletedTask;
            }
            """;

        var findings = Analyzer.AnalyzeFile("t.cs", src);

        Assert.Equal(3, findings.Count(f => f.RuleId == "TB001"));
    }

    [Fact]
    public void AnalyzeFiles_MultipleFiles_CombinesFindings()
    {
        const string file1 = """
            using System.Threading.Tasks;

            class C1
            {
                int M() => DoAsync().Result;
                Task<int> DoAsync() => Task.FromResult(1);
            }
            """;

        const string file2 = """
            using System.Threading.Tasks;

            class C2
            {
                async Task<int> M() => await DoAsync();
                Task<int> DoAsync() => Task.FromResult(1);
            }
            """;

        var findings = Analyzer.AnalyzeFiles(new[]
        {
            ("A.cs", file1),
            ("B.cs", file2),
        });

        Assert.Single(findings);
        Assert.Equal("A.cs", findings[0].FilePath);
    }

    // ===================== example files =====================

    [Fact]
    public void GoodExampleFile_HasNoFindings()
    {
        var path = FindExamplePath("Good.cs");
        var findings = Analyzer.AnalyzeFile(path, File.ReadAllText(path));
        Assert.Empty(findings);
    }

    [Fact]
    public void BadExampleFile_HasExpectedFindingCount()
    {
        var path = FindExamplePath("Bad.cs");
        var findings = Analyzer.AnalyzeFile(path, File.ReadAllText(path));

        Assert.Equal(8, findings.Count(f => f.RuleId == "TB001"));
    }

    private static string FindExamplePath(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "examples", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"could not locate examples/{fileName} above {dir}");
    }
}
