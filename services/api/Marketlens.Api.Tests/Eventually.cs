namespace Marketlens.Api.Tests;

/// <summary>Waits for background work (hosted workers) to reach a state, without fixed sleeps.</summary>
public static class Eventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static Task<bool> IsTrueAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        IsTrueAsync(() => Task.FromResult(condition()), timeout);

    /// <summary>For states only a query can see, such as rows a worker has written.</summary>
    public static async Task<bool> IsTrueAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }

        return await condition();
    }
}
