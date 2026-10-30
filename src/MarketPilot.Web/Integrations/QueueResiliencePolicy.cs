namespace MarketPilot.Web.Integrations;

public static class QueueResiliencePolicy
{
    public const int MaxAttempts = 8;

    public static bool IsTransientStatus(int status) =>
        status is 408 or 429 or 500 or 502 or 503 or 504;

    public static TimeSpan RetryDelay(int attempts, TimeSpan initial, TimeSpan maximum, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } requested && requested > TimeSpan.Zero)
            return requested > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : requested;

        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var delay = Math.Min(maximum.TotalMilliseconds, initial.TotalMilliseconds * Math.Pow(2, exponent));
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(Math.Min(maximum.TotalMilliseconds, delay * jitter));
    }
}
