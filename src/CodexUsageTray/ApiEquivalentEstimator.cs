using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageTray;

/// <summary>
/// Reconstructs an API-equivalent value from the token counters already stored
/// in Codex rollout files. Message contents are never retained or transmitted.
/// </summary>
public sealed class ApiEquivalentEstimator
{
    private const string SparkModel = "gpt-5.3-codex-spark";

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly IReadOnlyDictionary<string, ModelPrice> Prices =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6"] = new(5m, 0.50m, 30m),
            ["gpt-5.6-sol"] = new(5m, 0.50m, 30m),
            ["gpt-5.6-terra"] = new(2.50m, 0.25m, 15m),
            ["gpt-5.6-luna"] = new(1m, 0.10m, 6m),
            ["gpt-5.5"] = new(5m, 0.50m, 30m),
            ["gpt-5.4"] = new(2.50m, 0.25m, 15m),
            ["gpt-5.4-mini"] = new(0.75m, 0.075m, 4.50m),
            ["gpt-5.3-codex"] = new(1.75m, 0.175m, 14m),
            ["gpt-5.2"] = new(1.75m, 0.175m, 14m),
            ["gpt-5-codex"] = new(1.25m, 0.125m, 10m),
            ["codex-auto-review"] = new(1.75m, 0.175m, 14m),
            // Spark is a research preview with no final public API tariff.
            // GPT-5.3-Codex is the closest published proxy.
            [SparkModel] = new(1.75m, 0.175m, 14m)
        };

    private readonly string[] _roots;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, FileEstimate>? _cache;

    public ApiEquivalentEstimator(string? codexHome = null, string? cachePath = null)
    {
        var home = codexHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
        _roots =
        [
            Path.Combine(home, "sessions"),
            Path.Combine(home, "archived_sessions")
        ];

        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexMeter",
            "api-equivalent-cache-v4.json");
    }

    public async Task<ApiEquivalentEstimate?> EstimateAsync(
        long? authoritativeLifetimeTokens,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache ??= await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in EnumerateRollouts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cacheKey = CacheKey(path);
                livePaths.Add(cacheKey);

                var info = new FileInfo(path);
                if (_cache.TryGetValue(cacheKey, out var cached) &&
                    cached.Length == info.Length &&
                    cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                    cached.Models is { Count: > 0 })
                {
                    continue;
                }

                var parsed = await ParseFileAsync(path, info, cached, cancellationToken).ConfigureAwait(false);
                if (parsed is not null)
                {
                    _cache[cacheKey] = parsed;
                }
            }

            foreach (var stalePath in _cache.Keys.Where(path => !livePaths.Contains(path)).ToArray())
            {
                _cache.Remove(stalePath);
            }

            await SaveCacheAsync(_cache, cancellationToken).ConfigureAwait(false);

            decimal rawCost = 0;
            decimal todayRawCost = 0;
            long parsedTokens = 0;
            long todayTokens = 0;
            var pricedSessions = 0;
            var usesProxy = false;
            var unknownModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modelTotals = new Dictionary<ModelEffortKey, ModelTotals>();

            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var item in _cache.Values)
            {
                var isToday = item.RolloutDate == today;
                var pricedSession = false;
                foreach (var model in item.Models)
                {
                    var key = new ModelEffortKey(model.Model, model.Effort);
                    if (!modelTotals.TryGetValue(key, out var totals))
                    {
                        totals = new ModelTotals();
                        modelTotals[key] = totals;
                    }

                    totals.InputTokens += model.InputTokens;
                    totals.CachedInputTokens += model.CachedInputTokens;
                    totals.OutputTokens += model.OutputTokens;
                    totals.TotalTokens += model.TotalTokens;
                    totals.Sessions++;

                    if (!Prices.TryGetValue(model.Model, out var price))
                    {
                        unknownModels.Add(model.Model);
                        continue;
                    }

                    var uncachedInput = Math.Max(0, model.InputTokens - model.CachedInputTokens);
                    var itemCost = (uncachedInput / 1_000_000m * price.InputPerMillion) +
                                   (model.CachedInputTokens / 1_000_000m * price.CachedInputPerMillion) +
                                   (model.OutputTokens / 1_000_000m * price.OutputPerMillion);
                    totals.HasPrice = true;
                    totals.RawCost += itemCost;
                    rawCost += itemCost;
                    if (isToday)
                    {
                        todayRawCost += itemCost;
                    }
                    parsedTokens += model.TotalTokens;
                    pricedSession = true;
                    usesProxy |= model.Model.Equals(SparkModel, StringComparison.OrdinalIgnoreCase);
                }

                if (isToday)
                {
                    todayTokens += item.Models.Sum(model => model.TotalTokens);
                }
                if (pricedSession)
                {
                    pricedSessions++;
                }
            }

            if (pricedSessions == 0 || parsedTokens <= 0)
            {
                return null;
            }

            var scale = authoritativeLifetimeTokens is > 0
                ? authoritativeLifetimeTokens.Value / (double)parsedTokens
                : 1d;
            var observedTokens = modelTotals.Values.Sum(item => item.TotalTokens);
            var models = modelTotals
                .Select(pair => new ModelUsageBreakdown(
                    Model: pair.Key.Model,
                    Effort: pair.Key.Effort,
                    InputTokens: pair.Value.InputTokens,
                    CachedInputTokens: pair.Value.CachedInputTokens,
                    OutputTokens: pair.Value.OutputTokens,
                    TotalTokens: pair.Value.TotalTokens,
                    Sessions: pair.Value.Sessions,
                    DollarAmount: pair.Value.HasPrice
                        ? pair.Value.RawCost * (decimal)scale
                        : null,
                    TokenSharePercent: observedTokens > 0
                        ? pair.Value.TotalTokens / (double)observedTokens * 100d
                        : 0d))
                .OrderByDescending(item => item.TotalTokens)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Effort, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ApiEquivalentEstimate(
                DollarAmount: rawCost * (decimal)scale,
                TodayDollarAmount: todayRawCost,
                ParsedTokens: parsedTokens,
                TodayTokens: todayTokens,
                ParsedSessions: pricedSessions,
                ScaleFactor: scale,
                UsesProxyPricing: usesProxy,
                UnknownModels: unknownModels.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                Models: models);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static decimal CalculateCost(
        string model,
        long inputTokens,
        long cachedInputTokens,
        long outputTokens)
    {
        if (!Prices.TryGetValue(model, out var price))
        {
            return 0;
        }

        var uncachedInput = Math.Max(0, inputTokens - cachedInputTokens);
        return (uncachedInput / 1_000_000m * price.InputPerMillion) +
               (cachedInputTokens / 1_000_000m * price.CachedInputPerMillion) +
               (outputTokens / 1_000_000m * price.OutputPerMillion);
    }

    /// <summary>Removes only Codex Meter's derived estimate cache, never Codex sessions.</summary>
    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache = null;
            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache cleanup is optional; the monitor still remains read-only.
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> EnumerateRollouts()
    {
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in paths)
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    private static async Task<FileEstimate?> ParseFileAsync(
        string path,
        FileInfo info,
        FileEstimate? previous,
        CancellationToken cancellationToken)
    {
        var canAppend = previous is { Models.Count: > 0 } &&
                        previous.Length > 0 &&
                        previous.Length < info.Length;
        var startOffset = canAppend ? previous!.Length : 0;
        var currentModel = canAppend ? previous!.CurrentModel : string.Empty;
        var currentEffort = canAppend ? previous!.CurrentEffort : string.Empty;
        var lastUsage = canAppend ? previous!.LastUsage : default;
        var hasUsage = canAppend && previous!.HasUsage;
        var totals = canAppend
            ? previous!.Models.ToDictionary(
                item => new ModelEffortKey(item.Model, item.Effort),
                item => new FileModelTotals(item))
            : new Dictionary<ModelEffortKey, FileModelTotals>();

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        stream.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: startOffset == 0,
            bufferSize: 64 * 1024,
            leaveOpen: false);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal))
            {
                var context = TryReadContext(line);
                if (!string.IsNullOrWhiteSpace(context.Model))
                {
                    currentModel = context.Model;
                }
                if (!string.IsNullOrWhiteSpace(context.Effort))
                {
                    currentEffort = context.Effort;
                }
                continue;
            }

            if (!line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal) ||
                !TryReadUsage(line, out var usage))
            {
                continue;
            }

            var delta = hasUsage ? usage.DeltaFrom(lastUsage) : usage;
            lastUsage = usage;
            hasUsage = true;
            if (delta.TotalTokens <= 0)
            {
                continue;
            }

            var key = new ModelEffortKey(
                NormalizeModel(currentModel),
                NormalizeEffort(currentEffort));
            if (!totals.TryGetValue(key, out var modelTotals))
            {
                modelTotals = new FileModelTotals();
                totals[key] = modelTotals;
            }
            modelTotals.Add(delta);
        }

        var processedLength = stream.Position;
        if (totals.Count == 0)
        {
            return null;
        }

        var models = totals
            .Select(pair => pair.Value.ToEstimate(pair.Key))
            .OrderByDescending(item => item.TotalTokens)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Effort, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new FileEstimate(
            processedLength,
            info.LastWriteTimeUtc.Ticks,
            TryReadRolloutDate(path),
            currentModel,
            currentEffort,
            hasUsage,
            lastUsage,
            models);
    }

    private static string CacheKey(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }

    private static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim().ToLowerInvariant();

    private static string NormalizeEffort(string? effort) =>
        string.IsNullOrWhiteSpace(effort) ? "unspecified" : effort.Trim().ToLowerInvariant();

    private static DateOnly? TryReadRolloutDate(string path)
    {
        var fileName = Path.GetFileName(path);
        const string prefix = "rollout-";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            fileName.Length < prefix.Length + 10)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            fileName.AsSpan(prefix.Length, 10),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }

    private static (string? Model, string? Effort) TryReadContext(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var payload = document.RootElement.GetProperty("payload");
            var model = payload.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString()
                : null;
            var effort = payload.TryGetProperty("effort", out var effortElement)
                ? effortElement.GetString()
                : payload.TryGetProperty("reasoning_effort", out var reasoningEffortElement)
                    ? reasoningEffortElement.GetString()
                    : null;
            return (model, effort);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return default;
        }
    }

    private static bool TryReadUsage(string line, out TokenBreakdown usage)
    {
        usage = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            var total = document.RootElement
                .GetProperty("payload")
                .GetProperty("info")
                .GetProperty("total_token_usage");
            usage = new TokenBreakdown(
                total.GetProperty("input_tokens").GetInt64(),
                total.GetProperty("cached_input_tokens").GetInt64(),
                total.GetProperty("output_tokens").GetInt64(),
                total.GetProperty("total_tokens").GetInt64());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<Dictionary<string, FileEstimate>> LoadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return new Dictionary<string, FileEstimate>(StringComparer.OrdinalIgnoreCase);
            }

            await using var stream = File.OpenRead(_cachePath);
            var cache = await JsonSerializer.DeserializeAsync<Dictionary<string, FileEstimate>>(
                stream,
                CacheJsonOptions,
                cancellationToken).ConfigureAwait(false);
            return cache is null
                ? new Dictionary<string, FileEstimate>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileEstimate>(cache, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, FileEstimate>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveCacheAsync(
        Dictionary<string, FileEstimate> cache,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = new FileStream(
                _cachePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true);
            await JsonSerializer.SerializeAsync(stream, cache, CacheJsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The estimate remains usable in memory even if its optional cache cannot be written.
        }
    }

    private sealed record ModelPrice(
        decimal InputPerMillion,
        decimal CachedInputPerMillion,
        decimal OutputPerMillion);

    private sealed record FileEstimate(
        long Length,
        long LastWriteUtcTicks,
        DateOnly? RolloutDate,
        string CurrentModel,
        string CurrentEffort,
        bool HasUsage,
        TokenBreakdown LastUsage,
        IReadOnlyList<FileModelEstimate> Models);

    private sealed record FileModelEstimate(
        string Model,
        string Effort,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long TotalTokens);

    private readonly record struct ModelEffortKey(string Model, string Effort);

    private sealed class FileModelTotals
    {
        public FileModelTotals()
        {
        }

        public FileModelTotals(FileModelEstimate estimate)
        {
            InputTokens = estimate.InputTokens;
            CachedInputTokens = estimate.CachedInputTokens;
            OutputTokens = estimate.OutputTokens;
            TotalTokens = estimate.TotalTokens;
        }

        public long InputTokens { get; private set; }
        public long CachedInputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long TotalTokens { get; private set; }

        public void Add(TokenBreakdown usage)
        {
            InputTokens += usage.InputTokens;
            CachedInputTokens += usage.CachedInputTokens;
            OutputTokens += usage.OutputTokens;
            TotalTokens += usage.TotalTokens;
        }

        public FileModelEstimate ToEstimate(ModelEffortKey key) => new(
            key.Model,
            key.Effort,
            InputTokens,
            CachedInputTokens,
            OutputTokens,
            TotalTokens);
    }

    private sealed class ModelTotals
    {
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public int Sessions { get; set; }
        public bool HasPrice { get; set; }
        public decimal RawCost { get; set; }
    }

    private readonly record struct TokenBreakdown(
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long TotalTokens)
    {
        public TokenBreakdown DeltaFrom(TokenBreakdown previous)
        {
            if (InputTokens < previous.InputTokens ||
                CachedInputTokens < previous.CachedInputTokens ||
                OutputTokens < previous.OutputTokens ||
                TotalTokens < previous.TotalTokens)
            {
                return this;
            }

            return new TokenBreakdown(
                InputTokens - previous.InputTokens,
                CachedInputTokens - previous.CachedInputTokens,
                OutputTokens - previous.OutputTokens,
                TotalTokens - previous.TotalTokens);
        }
    }
}
