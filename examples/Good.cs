using System;
using System.Threading.Tasks;

namespace Taskblockslip.Examples;

public class AccountService
{
    /// <summary>
    /// Safe: 'await' on the Task&lt;T&gt; -- the correct, non-blocking pattern. Never flagged.
    /// </summary>
    public async Task<Account> GetAccountAsync(int accountId) =>
        await FetchAccountAsync(accountId);

    /// <summary>
    /// Safe: 'await' on a Task-returning call. Never flagged.
    /// </summary>
    public async Task SaveAccountAsync(Account account) =>
        await SaveAsync(account);

    /// <summary>
    /// Safe: 'await Task.WhenAll(...)' -- the correct non-blocking way to fan out and join several
    /// tasks. Never flagged.
    /// </summary>
    public async Task<Account[]> GetAccountsAsync(int[] accountIds)
    {
        var tasks = Array.ConvertAll(accountIds, FetchAccountAsync);
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Safe: 'await Task.Delay(...)' -- the correct non-blocking pause. Never flagged.
    /// </summary>
    public async Task PauseBrieflyAsync() =>
        await Task.Delay(TimeSpan.FromSeconds(1));

    /// <summary>
    /// Safe: a custom, unrelated type that merely happens to define its own '.Result' property and
    /// '.Wait()' method -- neither resolves to System.Threading.Tasks.Task/Task&lt;T&gt;, so the
    /// semantic check correctly leaves this alone. This is exactly why taskblockslip resolves the
    /// real static type via Roslyn's SemanticModel instead of matching on member names alone.
    /// </summary>
    public void UseUnrelatedResultAndWaitMembers()
    {
        var operation = new PollableOperation();
        operation.Wait();
        var outcome = operation.Result;
        Console.WriteLine(outcome);
    }

    public async Task<Account> FetchAccountAsync(int accountId)
    {
        await Task.Delay(1);
        return new Account(accountId);
    }

    public async Task SaveAsync(Account account)
    {
        await Task.Delay(1);
    }

    /// <summary>
    /// A hand-rolled type with its own unrelated 'Result' property and 'Wait()' method -- not
    /// System.Threading.Tasks.Task/Task&lt;T&gt; in any way, just a coincidental same-named shape.
    /// </summary>
    private sealed class PollableOperation
    {
        public string Result => "done";

        public void Wait()
        {
            // Polls some unrelated, already-synchronous resource. Nothing to do with Task at all.
        }
    }
}

public record Account(int Id);
