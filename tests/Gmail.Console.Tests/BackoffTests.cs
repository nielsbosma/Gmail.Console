namespace Gmail.Console.Tests;

using Gmail.Console.Infrastructure;

/// <summary>
/// Google meters two things under one error shape. The 250 units/second moving average
/// clears in about a second; the "Units per minute per user" budget only clears when the
/// window rolls over. Retrying the second kind on a one-second ladder burns every attempt
/// inside a window that was never going to admit them.
/// </summary>
public class BackoffTests
{
    private const string PerMinute =
        "Quota exceeded for quota metric 'Total Query Cost' and limit "
        + "'Units per minute per user' of service 'gmail.googleapis.com'";

    private const string Burst =
        "User-rate limit exceeded. Retry after 2026-09-18T09:00:00.000Z";

    [Fact]
    public void A_per_minute_quota_waits_long_enough_to_outlast_the_window()
    {
        // Four attempts have to add up to more than a minute, or the command reports a hard
        // failure for something that only needed waiting.
        var total = Enumerable.Range(1, 4)
            .Sum(attempt => GmailApiClient.Backoff(attempt, PerMinute).TotalSeconds);

        Assert.True(total > 60, $"four attempts waited only {total:0.0}s");
    }

    [Fact]
    public void A_burst_limit_does_not_wait_a_minute()
    {
        // The per-second average recovers immediately; treating it like the per-minute quota
        // would make an ordinary search feel hung.
        Assert.True(GmailApiClient.Backoff(1, Burst).TotalSeconds < 3);
    }

    [Fact]
    public void Every_wait_has_a_floor_so_parallel_fetches_do_not_stampede()
    {
        // Full jitter can draw ~0ms, which puts all --concurrency fetches back on the
        // limiter at once. Equal jitter keeps half the delay fixed.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var wait = GmailApiClient.Backoff(attempt, Burst);
            Assert.True(wait.TotalMilliseconds >= 500, $"attempt {attempt} waited {wait.TotalMilliseconds}ms");
        }
    }

    [Fact]
    public void Waits_are_capped_so_a_retry_cannot_outlive_the_command()
    {
        Assert.True(GmailApiClient.Backoff(20, PerMinute).TotalSeconds <= 65);
        Assert.True(GmailApiClient.Backoff(20, Burst).TotalSeconds <= 32);
    }
}
