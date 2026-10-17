namespace PaymentOrchestrator.IntegrationTests;

/// <summary>Polls until an asynchronous condition holds (background workers, message consumers).</summary>
internal static class Eventually
{
    public static async Task<T> Get<T>(Func<Task<T?>> probe, TimeSpan? timeout = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(100);
        }
    }
}
