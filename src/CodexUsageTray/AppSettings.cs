using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageTray;

public enum AppLanguage
{
    English,
    French
}

public enum StartupBehavior
{
    FullWindow,
    CompactWidget,
    TrayOnly,
    LastUsedView
}

public enum WindowView
{
    Full,
    Compact
}

/// <summary>Preferences kept locally for Codex Meter. They never contain Codex credentials or session data.</summary>
public sealed record AppSettings
{
    public AppLanguage Language { get; init; } = AppLanguage.English;
    public int RefreshIntervalMinutes { get; init; } = 1;
    public StartupBehavior StartupBehavior { get; init; } = StartupBehavior.FullWindow;
    public double UiScale { get; init; } = 1d;
    public bool CompactAlwaysOnTop { get; init; } = true;
    public bool ApiEquivalentEnabled { get; init; } = true;
    public WindowView LastView { get; init; } = WindowView.Full;
    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
}

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettingsService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexMeter",
            "settings.json");
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            return Normalize(settings ?? new AppSettings());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(SettingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences are optional; never make the monitor fail because they cannot be saved.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a failed atomic write.
            }
        }
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        var refreshInterval = settings.RefreshIntervalMinutes is 1 or 5 or 15
            ? settings.RefreshIntervalMinutes
            : 1;
        var language = Enum.IsDefined(settings.Language) ? settings.Language : AppLanguage.English;
        var startupBehavior = Enum.IsDefined(settings.StartupBehavior)
            ? settings.StartupBehavior
            : StartupBehavior.FullWindow;
        var lastView = Enum.IsDefined(settings.LastView) ? settings.LastView : WindowView.Full;
        var scale = double.IsFinite(settings.UiScale)
            ? Math.Round(Math.Clamp(settings.UiScale, 0.80d, 1.50d), 2)
            : 1d;

        return settings with
        {
            Language = language,
            RefreshIntervalMinutes = refreshInterval,
            StartupBehavior = startupBehavior,
            UiScale = scale,
            LastView = lastView,
            WindowLeft = IsValidCoordinate(settings.WindowLeft) ? settings.WindowLeft : null,
            WindowTop = IsValidCoordinate(settings.WindowTop) ? settings.WindowTop : null
        };
    }

    private static bool IsValidCoordinate(double? value) => value is { } coordinate && double.IsFinite(coordinate);
}
