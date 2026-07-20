using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    ("application version parsing", ApplicationVersionParsing),
    ("release update check", ReleaseUpdateCheck),
    ("release update staging", ReleaseUpdateStaging),
    ("update installer arguments", UpdateInstallerArguments),
    ("update file replacement", UpdateFileReplacement),
    ("cache omits local paths", CacheOmitsLocalPaths),
    ("cache clear stays local", CacheClearStaysLocal),
    ("settings defaults and recovery", SettingsDefaultsAndRecovery),
    ("settings persistence", SettingsPersistence),
    ("essential localizations", EssentialLocalizations),
    ("localization catalog completeness", LocalizationCatalogCompleteness),
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
    Equal("1.5K", UsageFormatter.CompactNumber(1500));
    Equal("2.25M", UsageFormatter.CompactNumber(2_250_000));
    Equal("1,5 k", UsageFormatter.CompactNumber(1500, AppLanguage.French));
    Equal("2,25 M", UsageFormatter.CompactNumber(2_250_000, AppLanguage.French));
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

static void ApplicationVersionParsing()
{
    Equal(true, AppVersion.TryParse("v0.2.0", out var stable));
    Equal(new AppVersion(0, 2, 0), stable);
    Equal(true, AppVersion.TryParse("1.4.3-beta.1+build", out var prerelease));
    Equal(new AppVersion(1, 4, 3), prerelease);
    Equal(false, AppVersion.TryParse("release-next", out _));
    Equal(true, new AppVersion(0, 3, 0).CompareTo(new AppVersion(0, 2, 9)) > 0);
}

static void ReleaseUpdateCheck()
{
    const string json = """
        {
          "tag_name": "v0.3.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "CodexMeter-win-x64-standalone.zip",
              "browser_download_url": "https://github.com/Ayoshy/Codex-Meter/releases/download/v0.3.0/CodexMeter-win-x64-standalone.zip",
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "size": 123456
            },
            {
              "name": "CodexMeter-win-arm64-standalone.zip",
              "browser_download_url": "https://github.com/Ayoshy/Codex-Meter/releases/download/v0.3.0/CodexMeter-win-arm64-standalone.zip",
              "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "size": 123456
            }
          ]
        }
        """;
    using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    });
    using var client = new HttpClient(handler);
    using var service = new ReleaseUpdateService(client);
    var update = service.CheckAsync(new AppVersion(0, 2, 0)).GetAwaiter().GetResult()
        ?? throw new Exception("expected update");
    Equal(new AppVersion(0, 3, 0), update.Version);
    Equal(
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "CodexMeter-win-arm64-standalone.zip"
            : "CodexMeter-win-x64-standalone.zip",
        update.Asset.Name);

    var current = service.CheckAsync(new AppVersion(0, 3, 0)).GetAwaiter().GetResult();
    Equal<AvailableUpdate?>(null, current);
}

static void ReleaseUpdateStaging()
{
    var root = Path.Combine(Path.GetTempPath(), $"codex-meter-update-{Guid.NewGuid():N}");
    try
    {
        var currentExecutable = Path.Combine(AppContext.BaseDirectory, "CodexMeter.exe");
        if (!File.Exists(currentExecutable))
        {
            throw new Exception("test app host unavailable");
        }

        byte[] archiveBytes;
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("CodexMeter.exe", CompressionLevel.NoCompression);
                using var destination = entry.Open();
                using var source = File.OpenRead(currentExecutable);
                source.CopyTo(destination);
            }
            archiveBytes = memory.ToArray();
        }

        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archiveBytes)
        });
        using var client = new HttpClient(handler);
        using var service = new ReleaseUpdateService(client, root);
        var phases = new List<UpdatePhase>();
        var progress = new ImmediateProgress<UpdateProgress>(value => phases.Add(value.Phase));
        var update = new AvailableUpdate(
            new AppVersion(0, 2, 0),
            "v0.2.0",
            new ReleaseAsset(
                "CodexMeter-win-x64-standalone.zip",
                new Uri("https://github.com/Ayoshy/Codex-Meter/releases/download/v0.2.0/CodexMeter-win-x64-standalone.zip"),
                digest,
                archiveBytes.Length));

        var staged = service.DownloadAsync(update, progress).GetAwaiter().GetResult();
        Equal(true, File.Exists(staged.ExecutablePath));
        Equal(new AppVersion(0, 2, 0), staged.Version);
        Equal(true, phases.Contains(UpdatePhase.Downloading));
        Equal(true, phases.Contains(UpdatePhase.Verifying));
        Equal(true, phases.Contains(UpdatePhase.Preparing));
        Equal(
            true,
            ReleaseUpdateService.DigestMatchesAsync(
                Path.Combine(staged.StagingDirectory, "CodexMeter.exe"),
                "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staged.ExecutablePath))))
                .GetAwaiter()
                .GetResult());
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void UpdateInstallerArguments()
{
    var staged = new StagedUpdate(
        new AppVersion(0, 2, 0),
        @"C:\updates\CodexMeter.exe",
        @"C:\updates");
    var startInfo = UpdateInstaller.CreateApplyStartInfo(
        staged,
        @"C:\apps\CodexMeter.exe",
        1234);

    Equal(@"C:\updates\CodexMeter.exe", startInfo.FileName);
    Equal(4, startInfo.ArgumentList.Count);
    Equal("--apply-update", startInfo.ArgumentList[0]);
    Equal(@"C:\apps\CodexMeter.exe", startInfo.ArgumentList[1]);
    Equal("1234", startInfo.ArgumentList[2]);
    Equal(@"C:\updates", startInfo.ArgumentList[3]);
}

static void UpdateFileReplacement()
{
    var root = Path.Combine(Path.GetTempPath(), $"codex-meter-replace-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var source = Path.Combine(root, "staged.exe");
        var target = Path.Combine(root, "CodexMeter.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "CodexMeter.exe"), source);
        File.WriteAllText(target, "old-version");

        var backup = UpdateInstaller.ReplaceExecutable(source, target);
        Equal(true, File.Exists(backup));
        Equal("old-version", File.ReadAllText(backup));
        Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
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
    Equal("7 days", UsageFormatter.WindowLabel(10080));
    Equal("5 hours", UsageFormatter.WindowLabel(300));
    Equal("90 minutes", UsageFormatter.WindowLabel(90));
    Equal("7 jours", UsageFormatter.WindowLabel(10080, AppLanguage.French));
    Equal("5 heures", UsageFormatter.WindowLabel(300, AppLanguage.French));
}

static void SettingsDefaultsAndRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"codex-meter-settings-{Guid.NewGuid():N}");
    var path = Path.Combine(root, "settings.json");
    try
    {
        var service = new AppSettingsService(path);
        var defaults = service.Load();
        Equal(AppLanguage.English, defaults.Language);
        Equal(1, defaults.RefreshIntervalMinutes);
        Equal(StartupBehavior.FullWindow, defaults.StartupBehavior);
        Equal(DockCorner.TopRight, defaults.CompactDockCorner);
        Equal(false, defaults.StartWithWindows);
        Equal(true, defaults.ApiEquivalentEnabled);
        Directory.CreateDirectory(root);
        File.WriteAllText(path, "{ not valid json");
        Equal(AppLanguage.English, service.Load().Language);
        var normalized = AppSettingsService.Normalize(new AppSettings
        {
            Language = (AppLanguage)99,
            RefreshIntervalMinutes = 2,
            StartupBehavior = (StartupBehavior)99,
            UiScale = 9d,
            LastView = (WindowView)99,
            CompactDockCorner = (DockCorner)99,
            ApiEquivalentEnabled = false
        });
        Equal(AppLanguage.English, normalized.Language);
        Equal(1, normalized.RefreshIntervalMinutes);
        Equal(StartupBehavior.FullWindow, normalized.StartupBehavior);
        Equal(1.5d, normalized.UiScale);
        Equal(WindowView.Full, normalized.LastView);
        Equal(DockCorner.TopRight, normalized.CompactDockCorner);
        var lastUsed = AppSettingsService.Normalize(new AppSettings
        {
            StartupBehavior = StartupBehavior.LastUsedView,
            LastView = WindowView.Compact
        });
        Equal(StartupBehavior.LastUsedView, lastUsed.StartupBehavior);
        Equal(WindowView.Compact, lastUsed.LastView);

        Equal(false, normalized.ApiEquivalentEnabled);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void SettingsPersistence()
{
    var root = Path.Combine(Path.GetTempPath(), $"codex-meter-settings-{Guid.NewGuid():N}");
    var path = Path.Combine(root, "settings.json");
    try
    {
        var service = new AppSettingsService(path);
        var expected = new AppSettings
        {
            Language = AppLanguage.French,
            RefreshIntervalMinutes = 15,
            StartupBehavior = StartupBehavior.LastUsedView,
            UiScale = 1.2d,
            CompactAlwaysOnTop = false,
            CompactDockCorner = DockCorner.BottomLeft,
            StartWithWindows = true,
            ApiEquivalentEnabled = false,
            LastView = WindowView.Compact,
            WindowLeft = 120d,
            WindowTop = 80d
        };
        service.Save(expected);
        Equal(expected, service.Load());
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void EssentialLocalizations()
{
    Equal("Settings", AppText.Get(AppLanguage.English, TextId.Settings));
    Equal("Paramètres", AppText.Get(AppLanguage.French, TextId.Settings));
    Equal("Connected · quotas synced", AppText.Get(AppLanguage.English, TextId.QuotasSynced));
    Equal("Connecté · quotas synchronisés", AppText.Get(AppLanguage.French, TextId.QuotasSynced));
    Equal("$12.50", UsageFormatter.Dollars(12.5m, AppLanguage.English));
    Equal("12,50 $", UsageFormatter.Dollars(12.5m, AppLanguage.French));
    Equal(
        "Quotas synced · local history unavailable.",
        AppText.Get(AppLanguage.English, TextId.QuotasSyncedHistoryUnavailable));
    Equal(
        "Could not read quotas. Refresh and make sure Codex is running.",
        AppText.Get(AppLanguage.English, TextId.QuotasUnavailable));
}

static void LocalizationCatalogCompleteness()
{
    foreach (var textId in Enum.GetValues<TextId>())
    {
        if (string.IsNullOrWhiteSpace(AppText.Get(AppLanguage.English, textId)) ||
            string.IsNullOrWhiteSpace(AppText.Get(AppLanguage.French, textId)))
        {
            throw new Exception($"Missing localization for {textId}");
        }
    }
}

static void CacheClearStaysLocal()
{
    var root = Path.Combine(Path.GetTempPath(), $"codex-meter-cache-{Guid.NewGuid():N}");
    var sessions = Path.Combine(root, "sessions", "2026", "07", "18");
    var cachePath = Path.Combine(root, "cache.json");
    Directory.CreateDirectory(sessions);
    try
    {
        var rollout = Path.Combine(sessions, "rollout-2026-07-18T12-00-00-00000000-0000-0000-0000-000000000000.jsonl");
        File.WriteAllText(rollout, "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.6-luna\"}}\n{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":2}}}}");
        var estimator = new ApiEquivalentEstimator(root, cachePath);
        estimator.EstimateAsync(null).GetAwaiter().GetResult();
        if (!File.Exists(cachePath)) throw new Exception("expected estimate cache");
        estimator.ClearCacheAsync().GetAwaiter().GetResult();
        if (File.Exists(cachePath)) throw new Exception("cache was not removed");
        if (!File.Exists(rollout)) throw new Exception("clearing cache touched a Codex session");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
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

sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}

sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
