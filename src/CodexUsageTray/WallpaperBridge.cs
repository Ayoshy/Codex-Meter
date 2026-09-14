using System.IO;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodexUsageTray;

// Deliberately project only display counters: no account identifiers, raw errors,
// session paths, authentication data or conversation content cross this boundary.
internal sealed record WallpaperLimit(string Id, string Name, RateLimitWindow? Primary, RateLimitWindow? Secondary);
internal sealed record WallpaperUsage(DateTimeOffset FetchedAt, IReadOnlyList<WallpaperLimit> Limits,
    long? TodayTokens, long? LifetimeTokens, long? StreakDays, long? ResetCredits,
    decimal? TodayApiDollars, decimal? TotalApiDollars, bool EstimatedPricing,
    IReadOnlyList<ModelUsageBreakdown> Models)
{
    public static WallpaperUsage From(UsageSnapshot source)
    {
        var hasToday = source.TokenUsage?.DailyUsageBuckets?.Any(bucket =>
            bucket.StartDate.StartsWith(source.FetchedAt.ToString("yyyy-MM-dd"), StringComparison.Ordinal)) == true;
        return new(source.FetchedAt,
            UsageFormatter.OrderedLimits(source.RateLimitResponse)
                .Select(pair => new WallpaperLimit(pair.Key, pair.Value.LimitName ?? pair.Key,
                    pair.Value.Primary, pair.Value.Secondary)).ToArray(),
            hasToday || source.ApiEquivalent is not null
                ? UsageFormatter.TokensToday(source.TokenUsage, source.FetchedAt, source.ApiEquivalent?.TodayTokens) : null,
            source.TokenUsage?.Summary.LifetimeTokens, source.TokenUsage?.Summary.CurrentStreakDays,
            source.RateLimitResponse.RateLimitResetCredits?.AvailableCount,
            source.ApiEquivalent?.TodayDollarAmount, source.ApiEquivalent?.DollarAmount,
            source.ApiEquivalent?.UsesProxyPricing == true || source.ApiEquivalent?.UnknownModels.Count > 0,
            source.ApiEquivalent?.Models ?? []);
    }
}

internal sealed record WallpaperMeterState(WallpaperUsage? Snapshot, bool Refreshing, string? Error, int RefreshIntervalMinutes);

internal sealed class WallpaperBridge : IAsyncDisposable
{
    internal const int Port = 19188;
    private readonly WebApplication _host;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    public WallpaperMeterState State { get; set; } = new(null, false, null, 15);
    public string Address => _host.Urls.Single();

    public static string LoadToken()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMeter");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "wallpaper-token.txt");
        if (File.Exists(path))
        {
            var saved = File.ReadAllText(path).Trim();
            if (saved.Length == 64 && saved.All(Uri.IsHexDigit)) return saved;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, token);
        return token;
    }

    public WallpaperBridge(string token, Func<Task> refresh, int port = Port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 1024;
        });
        _host = builder.Build();
        _host.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.AccessControlAllowOrigin = "*";
            if (context.Request.Method == "OPTIONS")
            {
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "X-Codex-Meter-Token";
                context.Response.StatusCode = 204;
                return;
            }
            if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(context.Request.Headers["X-Codex-Meter-Token"].ToString()),
                System.Text.Encoding.UTF8.GetBytes(token)))
            {
                context.Response.StatusCode = 401;
                return;
            }
            await next(context);
        });
        _host.MapGet("/state", () => State);
        _host.MapPost("/refresh", async () =>
        {
            if (!await _refreshGate.WaitAsync(0)) return Results.StatusCode(409);
            try
            {
                await refresh();
                return Results.Ok(State);
            }
            catch
            {
                return Results.Json(new { error = "Actualisation indisponible." }, statusCode: 503);
            }
            finally { _refreshGate.Release(); }
        });
    }

    public Task StartAsync() => _host.StartAsync();
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _host.StopAsync(timeout.Token); }
        catch (OperationCanceledException) { }
        await _host.DisposeAsync();
    }
}
