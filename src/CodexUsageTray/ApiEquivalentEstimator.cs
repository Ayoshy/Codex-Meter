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
    private const int InitialTailBytes = 512 * 1024;
    private const int MaximumTailBytes = 8 * 1024 * 1024;
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
            "api-equivalent-cache-v3.json");
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
                    !string.IsNullOrWhiteSpace(cached.Model))
                {
                    continue;
                }

                var parsed = await ParseTailAsync(path, info, cached, cancellationToken).ConfigureAwait(false);
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

            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var item in _cache.Values)
            {
                var isToday = item.RolloutDate == today;
                if (isToday)
                {
                    todayTokens += item.TotalTokens;
                }

                if (!Prices.TryGetValue(item.Model, out var price))
                {
                    unknownModels.Add(string.IsNullOrWhiteSpace(item.Model) ? "inconnu" : item.Model);
                    continue;
                }

                var uncachedInput = Math.Max(0, item.InputTokens - item.CachedInputTokens);
                var itemCost = (uncachedInput / 1_000_000m * price.InputPerMillion) +
                               (item.CachedInputTokens / 1_000_000m * price.CachedInputPerMillion) +
                               (item.OutputTokens / 1_000_000m * price.OutputPerMillion);
                rawCost += itemCost;
                if (isToday)
                {
                    todayRawCost += itemCost;
                }
                parsedTokens += item.TotalTokens;
                pricedSessions++;
                usesProxy |= item.Model.Equals(SparkModel, StringComparison.OrdinalIgnoreCase);
            }

            if (pricedSessions == 0 || parsedTokens <= 0)
            {
                return null;
            }

            var scale = authoritativeLifetimeTokens is > 0
                ? authoritativeLifetimeTokens.Value / (double)parsedTokens
                : 1d;

            return new ApiEquivalentEstimate(
                DollarAmount: rawCost * (decimal)scale,
                TodayDollarAmount: todayRawCost,
                ParsedTokens: parsedTokens,
                TodayTokens: todayTokens,
                ParsedSessions: pricedSessions,
                ScaleFactor: scale,
                UsesProxyPricing: usesProxy,
                UnknownModels: unknownModels.Order(StringComparer.OrdinalIgnoreCase).ToArray());
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

    private static async Task<FileEstimate?> ParseTailAsync(
        string path,
        FileInfo info,
        FileEstimate? previous,
        CancellationToken cancellationToken)
    {
        var requestedBytes = Math.Min(InitialTailBytes, info.Length);
        while (requestedBytes > 0)
        {
            var text = await ReadTailAsync(path, requestedBytes, cancellationToken).ConfigureAwait(false);
            if (TryParseLatestSnapshot(text, out var model, out var usage))
            {
                if (string.IsNullOrWhiteSpace(model))
                {
                    var head = await ReadHeadAsync(path, Math.Min(2 * 1024 * 1024, info.Length), cancellationToken)
                        .ConfigureAwait(false);
                    model = TryParseAnyModel(head) ?? previous?.Model ?? string.Empty;
                }

                return new FileEstimate(
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    string.IsNullOrWhiteSpace(model) ? previous?.Model ?? string.Empty : model,
                    TryReadRolloutDate(path),
                    usage.InputTokens,
                    usage.CachedInputTokens,
                    usage.OutputTokens,
                    usage.TotalTokens);
            }

            if (requestedBytes >= info.Length || requestedBytes >= MaximumTailBytes)
            {
                break;
            }

            requestedBytes = Math.Min(Math.Min(requestedBytes * 2, MaximumTailBytes), info.Length);
        }

        return previous is null
            ? null
            : previous with
            {
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                RolloutDate = TryReadRolloutDate(path)
            };
    }

    private static string CacheKey(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }

    private static async Task<string> ReadTailAsync(
        string path,
        long requestedBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        var length = (int)Math.Min(requestedBytes, stream.Length);
        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, offset);
    }

    private static async Task<string> ReadHeadAsync(
        string path,
        long requestedBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        var length = (int)Math.Min(requestedBytes, stream.Length);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, offset);
    }

    private static bool TryParseLatestSnapshot(
        string tail,
        out string model,
        out TokenBreakdown usage)
    {
        model = string.Empty;
        usage = default;
        var lines = tail.Split('\n');

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(model) && line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal))
            {
                model = TryReadModel(line) ?? string.Empty;
            }

            if (usage.TotalTokens == 0 && line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal))
            {
                usage = TryReadUsage(line);
            }

            if (!string.IsNullOrWhiteSpace(model) && usage.TotalTokens > 0)
            {
                return true;
            }
        }

        return usage.TotalTokens > 0;
    }

    private static string? TryParseAnyModel(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal))
            {
                continue;
            }

            var model = TryReadModel(line);
            if (!string.IsNullOrWhiteSpace(model))
            {
                return model;
            }
        }

        return null;
    }

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

    private static string? TryReadModel(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("payload").GetProperty("model").GetString();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static TokenBreakdown TryReadUsage(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var total = document.RootElement
                .GetProperty("payload")
                .GetProperty("info")
                .GetProperty("total_token_usage");
            return new TokenBreakdown(
                total.GetProperty("input_tokens").GetInt64(),
                total.GetProperty("cached_input_tokens").GetInt64(),
                total.GetProperty("output_tokens").GetInt64(),
                total.GetProperty("total_tokens").GetInt64());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return default;
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
        string Model,
        DateOnly? RolloutDate,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long TotalTokens);

    private readonly record struct TokenBreakdown(
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long TotalTokens);
}
