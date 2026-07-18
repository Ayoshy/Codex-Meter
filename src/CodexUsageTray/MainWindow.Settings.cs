using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using System.Windows.Media;

namespace CodexUsageTray;

public partial class MainWindow
{
    private static readonly IReadOnlyDictionary<string, TextId> StaticTextIds = new Dictionary<string, TextId>(StringComparer.Ordinal)
    {
        ["ACTIVITÉ"] = TextId.Activity,
        ["ACTIVITY"] = TextId.Activity,
        ["Connexion à Codex…"] = TextId.ConnectionToCodex,
        ["Connecting to Codex…"] = TextId.ConnectionToCodex,
        ["Connexion à Codex..."] = TextId.ConnectionToCodex,
        ["Connecting to Codex..."] = TextId.ConnectionToCodex,
        ["AUJOURD’HUI"] = TextId.Today,
        ["TODAY"] = TextId.Today,
        ["TOTAL"] = TextId.Total,
        ["SÉRIE"] = TextId.Streak,
        ["STREAK"] = TextId.Streak,
        ["activité"] = TextId.ActivityUnit,
        ["activity"] = TextId.ActivityUnit,
        ["Reset OpenAI disponible"] = TextId.ResetAvailable,
        ["OpenAI reset available"] = TextId.ResetAvailable,
        ["Crédit fourni par OpenAI"] = TextId.ResetProvidedByOpenAi,
        ["Credit provided by OpenAI"] = TextId.ResetProvidedByOpenAi,
        ["Pas encore actualisé"] = TextId.NeverRefreshed,
        ["Not refreshed yet"] = TextId.NeverRefreshed,
        ["QUOTA CODEX"] = TextId.CodexQuota,
        ["restant"] = TextId.Remaining,
        ["remaining"] = TextId.Remaining,
        ["En attente de synchronisation"] = TextId.PendingSync,
        ["Waiting for sync"] = TextId.PendingSync,
        ["tokens · "] = TextId.Tokens,
        ["Masquer dans le tray"] = TextId.Hide,
        ["Hide to tray"] = TextId.Hide
    };

    private static readonly IReadOnlyDictionary<string, TextId> TooltipIds = new Dictionary<string, TextId>(StringComparer.Ordinal)
    {
        ["Détacher en widget"] = TextId.DetachWidget,
        ["Detach as widget"] = TextId.DetachWidget,
        ["Actualiser"] = TextId.Refresh,
        ["Refresh"] = TextId.Refresh,
        ["Équivalent estimé aux tarifs API publics actuels. Calcul local."] = TextId.EstimateTooltip,
        ["Estimated using current public API prices. Local calculation only."] = TextId.EstimateTooltip,
        ["Revenir à la vue complète"] = TextId.BackToFullView,
        ["Back to full view"] = TextId.BackToFullView,
        ["Masquer"] = TextId.Hide,
        ["Hide"] = TextId.Hide
    };

    private AppSettings _settings = new();
    private Button? _settingsButton;

    public AppLanguage DisplayLanguage => _settings.Language;

    public event EventHandler<AppSettings>? SettingsChanged;
    public event EventHandler? SettingsRequested;

    private void InitializeSettings(AppSettings settings)
    {
        _settings = AppSettingsService.Normalize(settings);
        _zoom = _settings.UiScale;
        RootLayout.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        Width = FullWidth * _zoom;
        Height = FullHeight * _zoom;
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        Loaded += (_, _) =>
        {
            KeepInsideWorkingArea();
            AddSettingsButton();
            LocalizeStaticVisuals();
        };
        LocalizeStaticVisuals();
        SetEstimateVisibility();
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = AppSettingsService.Normalize(settings);
        LocalizeStaticVisuals();
        SetEstimateVisibility();
        if (Math.Abs(_zoom - _settings.UiScale) >= 0.001)
        {
            ApplyZoom(_settings.UiScale);
        }

        if (IsDocked)
        {
            ApplyTopmost(_settings.CompactAlwaysOnTop);
        }
    }

    public void SetDockedMode(bool docked)
    {
        if (docked && !IsDocked)
        {
            EnterDockedMode();
        }
        else if (!docked && IsDocked)
        {
            ExitDockedMode();
        }
    }

    public void PersistWindowState()
    {
        var settings = _settings with
        {
            LastView = IsDocked ? WindowView.Compact : WindowView.Full,
            WindowLeft = IsDocked || double.IsNaN(Left) ? _settings.WindowLeft : Left,
            WindowTop = IsDocked || double.IsNaN(Top) ? _settings.WindowTop : Top,
            UiScale = _zoom
        };
        PublishSettings(settings);
    }

    private void PublishSettings(AppSettings settings)
    {
        settings = AppSettingsService.Normalize(settings);
        if (settings == _settings)
        {
            return;
        }

        _settings = settings;
        SettingsChanged?.Invoke(this, settings);
    }

    private void SetEstimateVisibility()
    {
        var visibility = _settings.ApiEquivalentEnabled ? Visibility.Visible : Visibility.Collapsed;
        SetParentVisibility(TodayApiEquivalentText, visibility);
        SetParentVisibility(ApiEquivalentText, visibility);
        DockTodayApiEquivalentText.Visibility = visibility;
        DockApiEquivalentText.Visibility = visibility;
    }

    private static void SetParentVisibility(FrameworkElement element, Visibility visibility)
    {
        if (element.Parent is UIElement parent)
        {
            parent.Visibility = visibility;
        }
    }

    private void AddSettingsButton()
    {
        if (_settingsButton is not null || RefreshButton.Parent is not Panel toolbar)
        {
            return;
        }

        _settingsButton = new Button
        {
            Style = (Style)FindResource("ChromeButtonStyle"),
            Content = "⚙",
            FontSize = 15,
            ToolTip = AppText.Get(DisplayLanguage, TextId.Settings)
        };
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Children.Insert(Math.Max(0, toolbar.Children.IndexOf(RefreshButton)), _settingsButton);
    }

    private void LocalizeStaticVisuals()
    {
        foreach (var textBlock in FindVisualChildren<TextBlock>(this))
        {
            if (textBlock.Tag is not TextId id && !StaticTextIds.TryGetValue(textBlock.Text, out id))
            {
                continue;
            }

            textBlock.Tag = id;
            textBlock.Text = id == TextId.Tokens
                ? $"{AppText.Get(DisplayLanguage, id)} · "
                : AppText.Get(DisplayLanguage, id);
        }

        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (button.Content is string content && StaticTextIds.TryGetValue(content, out var id))
            {
                button.Content = AppText.Get(DisplayLanguage, id);
            }
        }

        foreach (var element in FindVisualChildren<FrameworkElement>(this))
        {
            if (element.ToolTip is string tooltip && TooltipIds.TryGetValue(tooltip, out var id))
            {
                element.ToolTip = AppText.Get(DisplayLanguage, id);
            }
        }

        if (_settingsButton is not null)
        {
            _settingsButton.ToolTip = AppText.Get(DisplayLanguage, TextId.Settings);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
