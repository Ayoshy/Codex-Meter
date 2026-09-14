using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CodexUsageTray;

internal static class WallpaperBridgeTests
{
    public static void Projection()
    {
        var now = DateTimeOffset.Now;
        var source = new UsageSnapshot(now, new()
        {
            RateLimits = new() { Primary = new() { UsedPercent = 37, WindowDurationMins = 300 } },
            RateLimitResetCredits = new() { AvailableCount = 2 }
        }, null, "private raw error with path and credentials");
        var missing = WallpaperUsage.From(source);
        Require(missing.TodayTokens is null && missing.LifetimeTokens is null, "Unavailable counters must not be zero");
        Require(missing.Limits[0].Primary?.UsedPercent == 37 && missing.ResetCredits == 2, "Quota projection");
        Require(!JsonSerializer.Serialize(missing).Contains("private raw error"), "Raw warnings must not cross bridge");
        var withUsage = source with { TokenUsage = new() { DailyUsageBuckets = [new() { StartDate = now.ToString("yyyy-MM-dd"), Tokens = 123 }], Summary = new() { LifetimeTokens = 999 } } };
        Require(WallpaperUsage.From(withUsage).TodayTokens == 123, "Official today counter");
        var estimate = new ApiEquivalentEstimate(10, 2, 50, 42, 1, 1, false, [], []);
        Require(WallpaperUsage.From(source with { ApiEquivalent = estimate }).TodayTokens == 42, "Local fallback");
        Require(WallpaperUsage.From(withUsage with { ApiEquivalent = estimate }).TodayTokens == 123, "Official counter wins");
    }

    public static async Task Http()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bridge = new WallpaperBridge("test-secret", async () =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("private raw failure");
        }, port: 0);
        await bridge.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(bridge.Address) };
        Require((await client.GetAsync("/state")).StatusCode == HttpStatusCode.Unauthorized, "Unpaired reads rejected");
        Require((await client.PostAsync("/refresh", null)).StatusCode == HttpStatusCode.Unauthorized, "Unpaired refresh rejected");
        Require((await client.SendAsync(new(HttpMethod.Options, "/refresh"))).StatusCode == HttpStatusCode.NoContent, "File-origin preflight");
        client.DefaultRequestHeaders.Add("X-Codex-Meter-Token", "test-secret");
        var state = await client.GetFromJsonAsync<WallpaperMeterState>("/state");
        Require(state?.Snapshot is null, "No fabricated initial data");
        Require((await client.PostAsync("/reset", null)).StatusCode == HttpStatusCode.NotFound, "No quota-reset endpoint");
        var first = client.PostAsync("/refresh", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Require((await client.PostAsync("/refresh", null)).StatusCode == HttpStatusCode.Conflict, "Concurrent refresh rejected"); }
        finally { release.TrySetResult(); }
        var failed = await first;
        Require(failed.StatusCode == HttpStatusCode.ServiceUnavailable, "Refresh failure status");
        Require(!(await failed.Content.ReadAsStringAsync()).Contains("private raw failure"), "Raw failure redacted");
        Require(failed.Headers.CacheControl?.NoStore == true, "No caching");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
