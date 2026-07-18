using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexUsageTray;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process;
    private StreamWriter? _input;
    private Task? _readLoop;
    private Task? _stderrLoop;
    private long _nextRequestId;
    private string? _lastStderrLine;
    private bool _initialized;

    public async Task<UsageSnapshot> ReadUsageAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var rateLimitJson = await CallAsync("account/rateLimits/read", parameters: null, cancellationToken)
            .ConfigureAwait(false);
        var rateLimits = Deserialize<GetAccountRateLimitsResponse>(rateLimitJson);

        GetAccountTokenUsageResponse? tokenUsage = null;
        string? tokenUsageWarning = null;
        try
        {
            var usageJson = await CallAsync("account/usage/read", parameters: null, cancellationToken)
                .ConfigureAwait(false);
            tokenUsage = Deserialize<GetAccountTokenUsageResponse>(usageJson);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            tokenUsageWarning = exception.Message;
        }

        return new UsageSnapshot(DateTimeOffset.Now, rateLimits, tokenUsage, tokenUsageWarning);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_initialized && _process is { HasExited: false })
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && _process is { HasExited: false })
            {
                return;
            }

            StopProcess();
            StartProcess();

            var initializeParams = new
            {
                clientInfo = new
                {
                    name = "codex-usage-tray",
                    title = "Codex Usage Tray",
                    version = "0.1.0"
                },
                capabilities = new
                {
                    experimentalApi = true,
                    requestAttestation = false,
                    optOutNotificationMethods = Array.Empty<string>()
                }
            };

            await CallCoreAsync("initialize", initializeParams, cancellationToken).ConfigureAwait(false);
            await SendAsync(new Dictionary<string, object?> { ["method"] = "initialized" }, cancellationToken)
                .ConfigureAwait(false);
            _initialized = true;
        }
        catch
        {
            StopProcess();
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCodexExecutable(),
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // app-server consumes newline-delimited JSON. A UTF-8 BOM before the first
            // request makes that first line invalid JSON, so the input encoding must be
            // explicitly BOM-free.
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de démarrer Codex app-server.");
        _input = _process.StandardInput;
        _input.AutoFlush = true;
        _readLoop = ReadLoopAsync(_process, _shutdown.Token);
        _stderrLoop = DrainStderrAsync(_process, _shutdown.Token);
    }

    private async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Codex app-server n'est pas initialisé.");
        }

        return await CallCoreAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> CallCoreAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Collision d'identifiant de requête Codex.");
        }

        var message = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["id"] = id
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        try
        {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            try
            {
                return await completion.Task
                    .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                var details = string.IsNullOrWhiteSpace(_lastStderrLine)
                    ? string.Empty
                    : $" Dernier message Codex : {_lastStderrLine}";
                throw new CodexAppServerException(
                    $"Codex n'a pas répondu à « {method} » dans le délai prévu.{details}",
                    exception);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(Dictionary<string, object?> message, CancellationToken cancellationToken)
    {
        var input = _input ?? throw new InvalidOperationException("Le canal Codex n'est pas disponible.");
        var json = JsonSerializer.Serialize(message);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await input.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
                {
                    continue;
                }

                if (!_pending.TryGetValue(id, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(new CodexAppServerException(DescribeError(error)));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetException(new CodexAppServerException("Réponse Codex incomplète."));
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                FailPending(BuildExitedException(process));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _lastStderrLine = line;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static T Deserialize<T>(JsonElement element)
    {
        return element.Deserialize<T>(JsonOptions)
            ?? throw new CodexAppServerException($"Codex a retourné une réponse {typeof(T).Name} vide.");
    }

    private static string DescribeError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "Erreur Codex inconnue.";
        }

        return error.ToString();
    }

    private Exception BuildExitedException(Process process)
    {
        var details = string.IsNullOrWhiteSpace(_lastStderrLine) ? string.Empty : $" {_lastStderrLine}";
        return new CodexAppServerException($"Codex app-server s'est arrêté (code {process.ExitCode}).{details}");
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(exception);
        }
    }

    private static string ResolveCodexExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_USAGE_TRAY_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var localPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");
        return File.Exists(localPath) ? localPath : "codex.exe";
    }

    private void StopProcess()
    {
        _initialized = false;
        _input = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            _process.Dispose();
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        StopProcess();
        FailPending(new OperationCanceledException("Codex Usage Tray s'arrête."));

        var tasks = new[] { _readLoop, _stderrLoop }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
        }

        _shutdown.Dispose();
        _startGate.Dispose();
        _writeGate.Dispose();
    }
}

public sealed class CodexAppServerException : Exception
{
    public CodexAppServerException(string message) : base(message)
    {
    }

    public CodexAppServerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
