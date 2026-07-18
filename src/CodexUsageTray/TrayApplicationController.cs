using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexUsageTray;

internal sealed class TrayApplicationController : IDisposable
{
    private readonly System.Windows.Application _application;
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _refreshTimer;
    private readonly CodexAppServerClient _client = new();
    private readonly ApiEquivalentEstimator _costEstimator = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly AppSettingsService _settingsService = new();
    private readonly Forms.ToolStripMenuItem _openMenuItem;
    private readonly Forms.ToolStripMenuItem _refreshMenuItem;
    private readonly Forms.ToolStripMenuItem _settingsMenuItem;
    private readonly Forms.ToolStripMenuItem _languageMenuItem;
    private readonly Forms.ToolStripMenuItem _englishMenuItem;
    private readonly Forms.ToolStripMenuItem _frenchMenuItem;
    private readonly Forms.ToolStripMenuItem _exitMenuItem;
    private AppSettings _settings;
    private UsageSnapshot? _lastSnapshot;
    private SettingsWindow? _settingsWindow;
    private bool _isExiting;
    private bool _hasSnapshot;

    public TrayApplicationController(System.Windows.Application application)
    {
        _application = application;
        _settings = _settingsService.Load();
        _window = new MainWindow(_settings);
        _window.RefreshRequested += async (_, _) => await RefreshAsync();
        _window.HideRequested += (_, _) =>
        {
            _window.PersistWindowState();
            _window.Hide();
        };
        _window.Closing += (_, eventArgs) =>
        {
            if (_isExiting)
            {
                return;
            }

            eventArgs.Cancel = true;
            _window.PersistWindowState();
            _window.Hide();
        };
        _window.SettingsChanged += OnSettingsChanged;
        _window.SettingsRequested += (_, _) => ShowSettings();

        var menu = new Forms.ContextMenuStrip();
        _openMenuItem = new Forms.ToolStripMenuItem();
        _openMenuItem.Click += (_, _) => ShowWindow();
        _refreshMenuItem = new Forms.ToolStripMenuItem();
        _refreshMenuItem.Click += async (_, _) => await RefreshAsync();
        _settingsMenuItem = new Forms.ToolStripMenuItem();
        _settingsMenuItem.Click += (_, _) => ShowSettings();
        _languageMenuItem = new Forms.ToolStripMenuItem();
        _englishMenuItem = new Forms.ToolStripMenuItem();
        _englishMenuItem.Click += (_, _) => ChangeLanguage(AppLanguage.English);
        _frenchMenuItem = new Forms.ToolStripMenuItem();
        _frenchMenuItem.Click += (_, _) => ChangeLanguage(AppLanguage.French);
        _languageMenuItem.DropDownItems.AddRange([_englishMenuItem, _frenchMenuItem]);
        _exitMenuItem = new Forms.ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => Exit();
        menu.Items.AddRange([
            _openMenuItem,
            _refreshMenuItem,
            _settingsMenuItem,
            _languageMenuItem,
            new Forms.ToolStripSeparator(),
            _exitMenuItem
        ]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = TrayIconRenderer.Create(null),
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                ShowWindow();
            }
        };

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(_settings.RefreshIntervalMinutes)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        ApplyTrayLocalization();
    }

    public void Start()
    {
        switch (_settings.StartupBehavior)
        {
            case StartupBehavior.CompactWidget:
                ShowCompactWindow();
                break;
            case StartupBehavior.LastUsedView:
                if (_settings.LastView == WindowView.Compact)
                {
                    ShowCompactWindow();
                }
                else
                {
                    ShowWindow();
                }
                break;
            case StartupBehavior.TrayOnly:
                break;
            default:
                ShowWindow();
                break;
        }
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    private void ShowWindow()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Focus();
    }

    private void ShowCompactWindow()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.SetDockedMode(true);
    }

    private void ShowSettings()
    {
        _settingsWindow ??= CreateSettingsWindow();
        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }
    private SettingsWindow CreateSettingsWindow()
    {
        var window = new SettingsWindow(_settings);
        if (_window.IsVisible)
        {
            window.Owner = _window;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        window.SettingsChanged += OnSettingsChanged;
        window.ClearCacheRequested += async (_, _) => await ClearEstimateCacheAsync();
        window.Closed += (_, _) => _settingsWindow = null;
        return window;
    }
    private void ChangeLanguage(AppLanguage language)
    {
        OnSettingsChanged(this, _settings with { Language = language });
    }
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var previous = _settings;
        _settings = AppSettingsService.Normalize(settings);
        _settingsService.Save(_settings);
        _window.ApplySettings(_settings);
        _settingsWindow?.ApplySettings(_settings);
        _refreshTimer.Interval = TimeSpan.FromMinutes(_settings.RefreshIntervalMinutes);
        ApplyTrayLocalization();
        if (!_settings.ApiEquivalentEnabled && previous.ApiEquivalentEnabled && _lastSnapshot is not null)
        {
            _lastSnapshot = _lastSnapshot with { ApiEquivalent = null };
            _window.Render(_lastSnapshot);
        }
        else if (_settings.ApiEquivalentEnabled && !previous.ApiEquivalentEnabled)
        {
            _ = RefreshAsync();
        }
        if (_lastSnapshot is not null)
        {
            _window.Render(_lastSnapshot);
            UpdateTray(_lastSnapshot);
        }
    }
    private async Task ClearEstimateCacheAsync()
    {
        await _costEstimator.ClearCacheAsync();
        _notifyIcon.ShowBalloonTip(3000, "Codex Meter", AppText.Get(_settings.Language, TextId.CacheCleared), Forms.ToolTipIcon.Info);
    }
    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            _window.SetLoading();
            var snapshot = await _client.ReadUsageAsync();
            if (_settings.ApiEquivalentEnabled)
            {
                try
                {
                    var estimate = await _costEstimator.EstimateAsync(snapshot.TokenUsage?.Summary.LifetimeTokens);
                    snapshot = snapshot with { ApiEquivalent = estimate };
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"API-equivalent estimate unavailable: {exception.Message}");
                }
            }
            _lastSnapshot = snapshot;
            _window.Render(snapshot);
            UpdateTray(snapshot);
            _hasSnapshot = true;
        }
        catch (Exception exception)
        {
            var message = FriendlyError(exception, _settings.Language);
            _window.ShowError(message);
            _notifyIcon.Text = TruncateTooltip($"Codex Meter · {AppText.Get(_settings.Language, TextId.SyncError)}");
            if (!_hasSnapshot)
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "Codex Meter",
                    message,
                    Forms.ToolTipIcon.Warning);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyTrayLocalization()
    {
        var language = _settings.Language;
        _openMenuItem.Text = AppText.Get(language, TextId.Open);
        _refreshMenuItem.Text = AppText.Get(language, TextId.Refresh);
        _settingsMenuItem.Text = AppText.Get(language, TextId.Settings);
        _languageMenuItem.Text = AppText.Get(language, TextId.Language);
        _englishMenuItem.Text = AppText.Get(language, TextId.English);
        _frenchMenuItem.Text = AppText.Get(language, TextId.French);
        _englishMenuItem.Checked = language == AppLanguage.English;
        _frenchMenuItem.Checked = language == AppLanguage.French;
        _exitMenuItem.Text = AppText.Get(language, TextId.Exit);
        if (_lastSnapshot is null)
        {
            _notifyIcon.Text = TruncateTooltip($"Codex Meter · {AppText.Get(language, TextId.ConnectionToCodex)}");
        }
    }
    private void UpdateTray(UsageSnapshot snapshot)
    {
        var mainWindow = snapshot.RateLimitResponse.RateLimits.Primary;
        var usedPercent = mainWindow?.UsedPercent ?? 0;
        var remainingPercent = Math.Max(0, 100 - usedPercent);

        var previousIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = TrayIconRenderer.Create(usedPercent);
        previousIcon?.Dispose();
        var culture = AppText.Culture(_settings.Language);
        _notifyIcon.Text = TruncateTooltip(
            $"Codex · {remainingPercent.ToString("0", culture)}% {AppText.Get(_settings.Language, TextId.Remaining)} · {snapshot.FetchedAt.ToString("HH:mm", culture)}");
    }

    private static string FriendlyError(Exception exception, AppLanguage language)
    {
        if (exception is System.ComponentModel.Win32Exception)
        {
            return AppText.Get(language, TextId.CodexUnavailable);
        }

        var message = exception.Message;
        if (message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return AppText.Get(language, TextId.CodexNotLoggedIn);
        }

        System.Diagnostics.Debug.WriteLine($"Could not read Codex quotas: {message}");
        return AppText.Get(language, TextId.QuotasUnavailable);
    }

    private static string TruncateTooltip(string text) => text.Length <= 63 ? text : text[..63];

    private void Exit()
    {
        _isExiting = true;
        _window.PersistWindowState();
        _refreshTimer.Stop();
        _notifyIcon.Visible = false;
        _window.Close();
        _application.Shutdown();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
        _refreshGate.Dispose();
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
