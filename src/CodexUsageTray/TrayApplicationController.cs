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
    private bool _isExiting;
    private bool _hasSnapshot;

    public TrayApplicationController(System.Windows.Application application)
    {
        _application = application;
        _window = new MainWindow();
        _window.RefreshRequested += async (_, _) => await RefreshAsync();
        _window.HideRequested += (_, _) => _window.Hide();
        _window.Closing += (_, eventArgs) =>
        {
            if (_isExiting)
            {
                return;
            }

            eventArgs.Cancel = true;
            _window.Hide();
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => ShowWindow());
        menu.Items.Add("Actualiser", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = TrayIconRenderer.Create(null),
            Text = "Codex Meter · connexion…",
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
            Interval = TimeSpan.FromMinutes(1)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        _window.Show();
        _window.Activate();
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
        if (_window.IsDocked)
        {
            _window.Topmost = true;
        }
        else
        {
            _window.Topmost = true;
            _window.Topmost = false;
        }
        _window.Focus();
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
            try
            {
                var estimate = await _costEstimator.EstimateAsync(snapshot.TokenUsage?.Summary.LifetimeTokens);
                snapshot = snapshot with { ApiEquivalent = estimate };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Cost reconstruction is optional and must never hide live quota data.
                System.Diagnostics.Debug.WriteLine($"API-equivalent estimate unavailable: {exception.Message}");
            }
            _window.Render(snapshot);
            UpdateTray(snapshot);
            _hasSnapshot = true;
        }
        catch (Exception exception)
        {
            var message = FriendlyError(exception);
            _window.ShowError(message);
            _notifyIcon.Text = TruncateTooltip("Codex Meter · erreur");
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

    private void UpdateTray(UsageSnapshot snapshot)
    {
        var mainWindow = snapshot.RateLimitResponse.RateLimits.Primary;
        var usedPercent = mainWindow?.UsedPercent ?? 0;
        var remainingPercent = Math.Max(0, 100 - usedPercent);

        var previousIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = TrayIconRenderer.Create(usedPercent);
        previousIcon?.Dispose();
        _notifyIcon.Text = TruncateTooltip($"Codex · {remainingPercent:0}% restant · {snapshot.FetchedAt:HH:mm}");
    }

    private static string FriendlyError(Exception exception)
    {
        if (exception is System.ComponentModel.Win32Exception)
        {
            return "Codex est introuvable. Installez Codex ou définissez CODEX_USAGE_TRAY_CODEX_PATH.";
        }

        var message = exception.Message;
        if (message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex n’est pas connecté. Lancez « codex login », puis actualisez.";
        }

        return $"Impossible de lire les quotas : {message}";
    }

    private static string TruncateTooltip(string text) => text.Length <= 63 ? text : text[..63];

    private void Exit()
    {
        _isExiting = true;
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
