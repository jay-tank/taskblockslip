using System;
using System.Threading.Tasks;

namespace Taskblockslip.Examples;

public class OrderService
{
    /// <summary>
    /// TB001: '.Result' on a Task&lt;T&gt;-returning method call blocks the calling thread instead of
    /// awaiting -- can deadlock under a captured SynchronizationContext, starves the thread pool
    /// under load otherwise.
    /// </summary>
    public Order GetOrder(int orderId) =>
        FetchOrderAsync(orderId).Result;

    /// <summary>
    /// TB001: '.Wait()' on a Task-returning call -- same blocking footgun, no result to unwrap.
    /// </summary>
    public void SaveOrder(Order order) =>
        SaveOrderAsync(order).Wait();

    /// <summary>
    /// TB001: '.GetAwaiter().GetResult()' is just as blocking as '.Result' -- a common "I read that
    /// .Result can rethrow wrapped in AggregateException so I'll use this instead" workaround that
    /// still blocks the thread.
    /// </summary>
    public Order GetOrderViaAwaiter(int orderId) =>
        FetchOrderAsync(orderId).GetAwaiter().GetResult();

    /// <summary>
    /// TB001: Task.WhenAll(...) still returns a Task, so '.Result' on it is exactly the same
    /// blocking pattern.
    /// </summary>
    public Order[] GetOrders(int[] orderIds)
    {
        var tasks = Array.ConvertAll(orderIds, FetchOrderAsync);
        return Task.WhenAll(tasks).Result;
    }

    /// <summary>
    /// TB001: Task.Delay(...) returns a Task; '.Wait()' on it blocks just the same as any other
    /// Task-returning call.
    /// </summary>
    public void PauseBriefly() =>
        Task.Delay(TimeSpan.FromSeconds(1)).Wait();

    /// <summary>
    /// TB001: nested inside a local function -- still a direct '.Result' on a Task&lt;T&gt;, still
    /// flagged regardless of nesting depth.
    /// </summary>
    public Order GetOrderViaLocalFunction(int orderId)
    {
        Order Inner() => FetchOrderAsync(orderId).Result;
        return Inner();
    }

    /// <summary>
    /// TB001: nested inside a lambda -- same pattern, same blocking problem.
    /// </summary>
    public void FireAndBlock(int orderId)
    {
        Action work = () => FetchOrderAsync(orderId).Wait();
        work();
    }

    /// <summary>
    /// TB001: fully qualified System.Threading.Tasks.Task&lt;T&gt; variant -- the semantic check
    /// resolves this the same as the "using"-shortened form above.
    /// </summary>
    public Order GetOrderFullyQualified(int orderId)
    {
        System.Threading.Tasks.Task<Order> task = FetchOrderAsync(orderId);
        return task.Result;
    }

    public async Task<Order> FetchOrderAsync(int orderId)
    {
        await Task.Delay(1);
        return new Order(orderId);
    }

    public async Task SaveOrderAsync(Order order)
    {
        await Task.Delay(1);
    }
}

public record Order(int Id);
