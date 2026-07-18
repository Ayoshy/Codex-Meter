using System.Text.Json;
using CodexUsageTray;

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    await using var client = new CodexAppServerClient();
    var snapshot = await client.ReadUsageAsync();
    var estimate = await new ApiEquivalentEstimator().EstimateAsync(snapshot.TokenUsage?.Summary.LifetimeTokens);
    Console.WriteLine(
        $"LIVE plan={snapshot.RateLimitResponse.RateLimits.PlanType} " +
        $"used={snapshot.RateLimitResponse.RateLimits.Primary?.UsedPercent:0.##}% " +
        $"limits={UsageFormatter.OrderedLimits(snapshot.RateLimitResponse).Count} " +
        $"tokens={snapshot.TokenUsage?.Summary.LifetimeTokens} " +
        $"today={UsageFormatter.TokensToday(snapshot.TokenUsage, snapshot.FetchedAt, estimate?.TodayTokens)} " +
        $"todayApiEquivalent={estimate?.TodayDollarAmount:0.00} " +
        $"apiEquivalent={estimate?.DollarAmount:0.00} " +
        $"sessions={estimate?.ParsedSessions}");
    foreach (var bucket in snapshot.TokenUsage?.DailyUsageBuckets?.TakeLast(3) ?? [])
    {
        Console.WriteLine($"BUCKET start={bucket.StartDate} tokens={bucket.Tokens}");
    }
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("parse rate limits", ParseRateLimits),
    ("compact token formatting", CompactTokenFormatting),
    ("dollar formatting", DollarFormatting),
    ("API equivalent pricing", ApiEquivalentPricing),
    ("cache omits local paths", CacheOmitsLocalPaths),
    ("window labels", WindowLabels),
    ("today token lookup", TodayTokenLookup),
    ("main limit ordering", MainLimitOrdering)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void ParseRateLimits()
{
    const string json = """
        {
          "rateLimits": {
            "limitId": "codex",
            "limitName": null,
            "primary": { "usedPercent": 42.5, "windowDurationMins": 10080, "resetsAt": 1784784759 },
            "secondary": null,
            "credits": { "hasCredits": false, "unlimited": false, "balance": "0" },
            "planType": "pro"
          },
          "rateLimitsByLimitId": null,
          "rateLimitResetCredits": { "availableCount": 2, "credits": [] }
        }
        """;

    var response = JsonSerializer.Deserialize<GetAccountRateLimitsResponse>(json)
        ?? throw new Exception("deserialization returned null");
    Equal("codex", response.RateLimits.LimitId);
    Equal(42.5, response.RateLimits.Primary?.UsedPercent);
    Equal(2L, response.RateLimitResetCredits?.AvailableCount);
}

static void CompactTokenFormatting()
{
    Equal("999", UsageFormatter.CompactNumber(999));
    Equal("1,5 k", UsageFormatter.CompactNumber(1500));
    Equal("2,25 M", UsageFormatter.CompactNumber(2_250_000));
    Equal("—", UsageFormatter.CompactNumber(null));
}

static void DollarFormatting()
{
    Equal("$2,365", UsageFormatter.Dollars(2365m));
    Equal("$12.50", UsageFormatter.Dollars(12.5m));
    Equal("—", UsageFormatter.Dollars(null));
}

static void ApiEquivalentPricing()
{
    // GPT-5.6 Sol: 1M uncached input + 1M cached input + 1M output.
    Equal(35.5m, ApiEquivalentEstimator.CalculateCost("gpt-5.6-sol", 2_000_000, 1_000_000, 1_000_000));
    Equal(0m, ApiEquivalentEstimator.CalculateCost("unknown-model", 1_000_000, 0, 0));
}

static void CacheOmitsLocalPaths()
{
    var root = Path.Combine(Path.GetTempPath(), $"codex-meter-test-{Guid.NewGuid():N}");
    var sessionDirectory = Path.Combine(root, "sessions", "2026", "07", "18");
    var cachePath = Path.Combine(root, "cache.json");
    Directory.CreateDirectory(sessionDirectory);

    try
    {
        var rolloutPath = Path.Combine(
            sessionDirectory,
            "rollout-2026-07-18T12-00-00-00000000-0000-0000-0000-000000000000.jsonl");
        File.WriteAllText(
            rolloutPath,
            """
            {"type":"turn_context","payload":{"model":"gpt-5.6-luna"}}
            {"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":30,"total_tokens":130}}}}
            """);

        var estimate = new ApiEquivalentEstimator(root, cachePath)
            .EstimateAsync(authoritativeLifetimeTokens: null)
            .GetAwaiter()
            .GetResult();
        var cache = File.ReadAllText(cachePath);

        Equal(130L, estimate?.ParsedTokens);
        if (cache.Contains(root, StringComparison.OrdinalIgnoreCase) ||
            cache.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("cache contains a local path or Windows user name");
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void WindowLabels()
{
    Equal("7 jours", UsageFormatter.WindowLabel(10080));
    Equal("5 heures", UsageFormatter.WindowLabel(300));
    Equal("90 minutes", UsageFormatter.WindowLabel(90));
}

static void TodayTokenLookup()
{
    var usage = new GetAccountTokenUsageResponse
    {
        DailyUsageBuckets =
        [
            new AccountTokenUsageDailyBucket { StartDate = "2026-07-15", Tokens = 100 },
            new AccountTokenUsageDailyBucket { StartDate = "2026-07-16", Tokens = 250 }
        ]
    };
    Equal(250L, UsageFormatter.TokensToday(usage, new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)));

    var delayedUsage = new GetAccountTokenUsageResponse
    {
        DailyUsageBuckets =
        [
            new AccountTokenUsageDailyBucket { StartDate = "2026-07-15T00:00:00Z", Tokens = 100 }
        ]
    };
    Equal(321L, UsageFormatter.TokensToday(
        delayedUsage,
        new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
        localFallback: 321));
}

static void MainLimitOrdering()
{
    var response = new GetAccountRateLimitsResponse
    {
        RateLimitsByLimitId = new Dictionary<string, RateLimitSnapshot>
        {
            ["spark"] = new() { LimitName = "Spark" },
            ["codex"] = new() { LimitName = null }
        }
    };
    Equal("codex", UsageFormatter.OrderedLimits(response)[0].Key);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"expected '{expected}', got '{actual}'");
    }
}
