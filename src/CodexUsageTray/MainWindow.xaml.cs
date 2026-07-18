using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using InputKey = System.Windows.Input.Key;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;
using Forms = System.Windows.Forms;

namespace CodexUsageTray;

public partial class MainWindow : Window
{
    private const double FullWidth = 500;
    private const double FullHeight = 740;
    private const double DockedWidth = 400;
    private const double DockedHeight = 190;
    private const double DockMargin = 12;
    private const double MinimumZoom = 0.80;
    private const double MaximumZoom = 1.50;
    private const double ZoomStep = 0.10;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private CultureInfo DisplayCulture => AppText.Culture(DisplayLanguage);
    private static readonly Brush MutedBrush = BrushFrom("#8C97A8");
    private static readonly Brush SubtleBrush = BrushFrom("#616D7E");
    private static readonly Brush CardBrush = BrushFrom("#E8161C25");
    private static readonly Brush CardBorderBrush = BrushFrom("#293240");
    private static readonly Brush GoodBrush = BrushFrom("#55E6A5");
    private static readonly Brush CyanBrush = BrushFrom("#5BC8FF");
    private static readonly Brush WarningBrush = BrushFrom("#F6B855");
    private static readonly Brush DangerBrush = BrushFrom("#FF727D");
    private System.Windows.Rect _restoreBounds = new(100, 100, FullWidth, FullHeight);
    private bool _restoreTopmost;
    private double _zoom = 1d;

    public bool IsDocked { get; private set; }

    public event EventHandler? RefreshRequested;
    public event EventHandler? HideRequested;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        InitializeSettings(settings);
    }

    private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement frame)
        {
            frame.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                22,
                22);
        }
    }

    public void SetLoading()
    {
        SetRefreshEnabled(false);
        StatusBorder.Background = BrushFrom("#18231F");
        StatusBorder.BorderBrush = BrushFrom("#344A40");
        StatusDot.Fill = CyanBrush;
        StatusText.Foreground = BrushFrom("#B9E7FA");
        StatusText.Text = AppText.Get(DisplayLanguage, TextId.SyncingWithCodex);
        DockStatusDot.Fill = CyanBrush;
        DockUpdatedText.Text = AppText.Get(DisplayLanguage, TextId.SyncingWithCodex);
    }

    public void ShowError(string message)
    {
        SetRefreshEnabled(true);
        StatusBorder.Background = BrushFrom("#321C22");
        StatusBorder.BorderBrush = BrushFrom("#68343D");
        StatusDot.Fill = DangerBrush;
        StatusText.Foreground = BrushFrom("#FFB6BD");
        StatusText.Text = message;
        LastUpdatedText.Text = AppText.Get(DisplayLanguage, TextId.RefreshFailed);
        DockStatusDot.Fill = DangerBrush;
        DockUpdatedText.Text = AppText.Get(DisplayLanguage, TextId.SyncError);
    }

    public void Render(UsageSnapshot snapshot)
    {
        SetRefreshEnabled(true);
        StatusBorder.Background = BrushFrom("#16271F");
        StatusBorder.BorderBrush = BrushFrom("#2B5A43");
        StatusDot.Fill = GoodBrush;
        StatusText.Foreground = BrushFrom("#A7F3D0");
        if (!string.IsNullOrWhiteSpace(snapshot.TokenUsageWarning))
        {
            System.Diagnostics.Debug.WriteLine($"Local token history unavailable: {snapshot.TokenUsageWarning}");
        }
        StatusText.Text = snapshot.TokenUsageWarning is null
            ? AppText.Get(DisplayLanguage, TextId.QuotasSynced)
            : AppText.Get(DisplayLanguage, TextId.QuotasSyncedHistoryUnavailable);
        DockStatusDot.Fill = GoodBrush;

        LimitsPanel.Children.Clear();
        var limits = UsageFormatter.OrderedLimits(snapshot.RateLimitResponse);
        for (var index = 0; index < limits.Count; index++)
        {
            var pair = limits[index];
            LimitsPanel.Children.Add(CreateLimitCard(pair.Key, pair.Value, isPrimary: index == 0));
        }

        var todayTokens = UsageFormatter.TokensToday(
            snapshot.TokenUsage,
            snapshot.FetchedAt,
            snapshot.ApiEquivalent?.TodayTokens);
        TodayTokensText.Text = UsageFormatter.CompactNumber(todayTokens, DisplayLanguage);
        TodayApiEquivalentText.Text = UsageFormatter.Dollars(snapshot.ApiEquivalent?.TodayDollarAmount, DisplayLanguage);
        LifetimeTokensText.Text = UsageFormatter.CompactNumber(snapshot.TokenUsage?.Summary.LifetimeTokens, DisplayLanguage);
        LifetimeTokensText.ToolTip = snapshot.TokenUsage?.Summary.LifetimeTokens?.ToString("N0", DisplayCulture);
        ApiEquivalentText.Text = UsageFormatter.Dollars(snapshot.ApiEquivalent?.DollarAmount, DisplayLanguage);
        var estimateDetails = BuildEstimateTooltip(snapshot.ApiEquivalent);
        ApiEquivalentText.ToolTip = estimateDetails;
        TodayApiEquivalentText.ToolTip = estimateDetails;
        SetEstimateVisibility();

        var mainWindow = snapshot.RateLimitResponse.RateLimits.Primary;
        var mainUsedPercent = Math.Clamp(mainWindow?.UsedPercent ?? 0, 0, 100);
        var mainRemainingPercent = Math.Max(0, 100 - mainUsedPercent);
        var dockAccent = mainUsedPercent switch
        {
            >= 95 => DangerBrush,
            >= 80 => WarningBrush,
            _ => GoodBrush
        };
        DockRemainingText.Text = mainRemainingPercent.ToString("0", DisplayCulture);
        DockRemainingText.Foreground = dockAccent;
        DockProgress.Value = mainUsedPercent;
        DockProgress.Foreground = dockAccent;
        DockTodayTokensText.Text = UsageFormatter.CompactNumber(todayTokens, DisplayLanguage);
        DockTodayApiEquivalentText.Text = UsageFormatter.Dollars(snapshot.ApiEquivalent?.TodayDollarAmount, DisplayLanguage);
        DockTotalTokensText.Text = UsageFormatter.CompactNumber(snapshot.TokenUsage?.Summary.LifetimeTokens, DisplayLanguage);
        DockApiEquivalentText.Text = UsageFormatter.Dollars(snapshot.ApiEquivalent?.DollarAmount, DisplayLanguage);
        DockApiEquivalentText.ToolTip = estimateDetails;
        DockTodayApiEquivalentText.ToolTip = estimateDetails;
        DockUpdatedText.Text = AppText.Format(DisplayLanguage, TextId.SyncedAt, snapshot.FetchedAt.ToString("HH:mm:ss", DisplayCulture));

        var streak = snapshot.TokenUsage?.Summary.CurrentStreakDays;
        StreakText.Text = streak is null ? "—" : streak.Value.ToString(DisplayCulture);

        var resetCredits = snapshot.RateLimitResponse.RateLimitResetCredits?.AvailableCount;
        ResetCreditsText.Text = resetCredits?.ToString(DisplayCulture) ?? "—";
        ResetCaptionText.Text = resetCredits switch
        {
            null => AppText.Get(DisplayLanguage, TextId.ResetInformationUnavailable),
            0 => AppText.Get(DisplayLanguage, TextId.NoResetCredits),
            1 => AppText.Get(DisplayLanguage, TextId.OneResetCredit),
            _ => AppText.Format(DisplayLanguage, TextId.ManyResetCredits, resetCredits)
        };
        LastUpdatedText.Text = AppText.Format(DisplayLanguage, TextId.SyncedAt, snapshot.FetchedAt.ToString("HH:mm:ss", DisplayCulture));
    }

    private string BuildEstimateTooltip(ApiEquivalentEstimate? estimate)
    {
        if (estimate is null)
        {
            return AppText.Get(DisplayLanguage, TextId.EstimateUnavailable);
        }

        var proxy = estimate.UsesProxyPricing ? AppText.Get(DisplayLanguage, TextId.ProxyPricing) : string.Empty;
        var warning = estimate.UnknownModels.Count == 0 ? string.Empty : AppText.Format(DisplayLanguage, TextId.UnknownModels, string.Join(", ", estimate.UnknownModels));
        return AppText.Format(DisplayLanguage, TextId.EstimateDetails, estimate.ParsedSessions) + proxy + warning;
    }

    private Border CreateLimitCard(string id, RateLimitSnapshot limit, bool isPrimary)
    {
        var window = limit.Primary;
        var usedPercent = Math.Clamp(window?.UsedPercent ?? 0, 0, 100);
        var remainingPercent = Math.Max(0, 100 - usedPercent);
        var accent = usedPercent switch
        {
            >= 95 => DangerBrush,
            >= 80 => WarningBrush,
            _ when isPrimary => GoodBrush,
            _ => CyanBrush
        };

        var title = string.IsNullOrWhiteSpace(limit.LimitName)
            ? id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : id
            : limit.LimitName;
        var resetAt = UsageFormatter.ResetTime(window?.ResetsAt);
        var resetText = resetAt is null
            ? AppText.Get(DisplayLanguage, TextId.UnknownDeadline)
            : AppText.Format(DisplayLanguage, TextId.ResetsAt, resetAt.Value.ToString("ddd d MMM · HH:mm", DisplayCulture));

        var content = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = isPrimary ? 17 : 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#F6F8FB")
        });
        heading.Children.Add(new TextBlock
        {
            Text = AppText.Get(DisplayLanguage, isPrimary ? TextId.MainQuota : TextId.ModelQuota),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = SubtleBrush,
            Margin = new Thickness(0, 4, 0, 0)
        });
        header.Children.Add(heading);

        var windowPill = new Border
        {
            Background = BrushFrom(isPrimary ? "#213B33" : "#202F3C"),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = UsageFormatter.WindowLabel(window?.WindowDurationMins, DisplayLanguage).ToUpper(DisplayCulture),
                Foreground = accent,
                FontSize = 9,
                FontWeight = FontWeights.Bold
            }
        };
        Grid.SetColumn(windowPill, 1);
        header.Children.Add(windowPill);
        content.Children.Add(header);

        var metric = new Grid { Margin = new Thickness(0, isPrimary ? 17 : 13, 0, 0) };
        metric.ColumnDefinitions.Add(new ColumnDefinition());
        metric.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var remaining = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        remaining.Children.Add(new TextBlock
        {
            Text = remainingPercent.ToString("0", DisplayCulture),
            FontSize = isPrimary ? 38 : 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
            LineHeight = isPrimary ? 40 : 28
        });
        remaining.Children.Add(new TextBlock
        {
            Text = AppText.Get(DisplayLanguage, TextId.PercentRemaining),
            FontSize = isPrimary ? 12 : 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
            Margin = new Thickness(6, 0, 0, isPrimary ? 6 : 3),
            VerticalAlignment = VerticalAlignment.Bottom
        });
        metric.Children.Add(remaining);

        var reset = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        reset.Children.Add(new TextBlock
        {
            Text = resetText,
            Foreground = MutedBrush,
            FontSize = 10.5,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        });
        reset.Children.Add(new TextBlock
        {
            Text = AppText.Format(DisplayLanguage, TextId.PercentUsed, usedPercent.ToString("0.#", DisplayCulture)),
            Foreground = SubtleBrush,
            FontSize = 9.5,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        });
        Grid.SetColumn(reset, 1);
        metric.Children.Add(reset);
        content.Children.Add(metric);

        content.Children.Add(new System.Windows.Controls.ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = usedPercent,
            Foreground = accent,
            Margin = new Thickness(0, isPrimary ? 14 : 11, 0, 0)
        });

        return new Border
        {
            Background = CardBrush,
            BorderBrush = isPrimary ? BrushFrom("#385347") : CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(isPrimary ? 18 : 15),
            Padding = new Thickness(isPrimary ? 18 : 15),
            Margin = new Thickness(0, 0, 0, 11),
            Effect = new DropShadowEffect
            {
                BlurRadius = isPrimary ? 18 : 12,
                ShadowDepth = 4,
                Opacity = isPrimary ? 0.2 : 0.12,
                Color = Colors.Black
            },
            Child = content
        };
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        return new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }

    private void SetRefreshEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        DockRefreshButton.IsEnabled = enabled;
    }

    private void ToggleDockMode()
    {
        if (IsDocked)
        {
            ExitDockedMode();
        }
        else
        {
            EnterDockedMode();
        }
    }

    private void EnterDockedMode()
    {
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        _restoreBounds = new System.Windows.Rect(Left, Top, Width, Height);
        _restoreTopmost = Topmost;

        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var transformFromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        FullView.Visibility = Visibility.Collapsed;
        DockedView.Visibility = Visibility.Visible;
        Width = DockedWidth * _zoom;
        Height = DockedHeight * _zoom;
        PositionDockedWindow(screen, transformFromDevice);
        ShowInTaskbar = false;
        ApplyTopmost(_settings.CompactAlwaysOnTop);
        IsDocked = true;
        Activate();
        PersistWindowState();
    }

    private void PositionDockedWindow()
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var transformFromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        PositionDockedWindow(screen, transformFromDevice);
    }

    private void PositionDockedWindow(Forms.Screen screen, Matrix transformFromDevice)
    {
        var topLeft = transformFromDevice.Transform(
            new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transformFromDevice.Transform(
            new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var right = bottomRight.X - Width - DockMargin;
        var bottom = bottomRight.Y - Height - DockMargin;

        (Left, Top) = _settings.CompactDockCorner switch
        {
            DockCorner.TopLeft => (topLeft.X + DockMargin, topLeft.Y + DockMargin),
            DockCorner.BottomLeft => (topLeft.X + DockMargin, bottom),
            DockCorner.BottomRight => (right, bottom),
            _ => (right, topLeft.Y + DockMargin)
        };
    }

    private void ExitDockedMode()
    {
        DockedView.Visibility = Visibility.Collapsed;
        FullView.Visibility = Visibility.Visible;
        Width = FullWidth * _zoom;
        Height = FullHeight * _zoom;
        Left = _restoreBounds.Left;
        Top = _restoreBounds.Top;
        ShowInTaskbar = true;
        ApplyTopmost(_restoreTopmost);
        IsDocked = false;
        KeepInsideWorkingArea();
        Activate();
        PersistWindowState();
    }

    private void ApplyTopmost(bool enabled)
    {
        Topmost = enabled;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            enabled ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleDockButton_Click(object sender, RoutedEventArgs e) => ToggleDockMode();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void Window_KeyDown(object sender, InputKeyEventArgs e)
    {
        if (e.Key == InputKey.Escape)
        {
            HideRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        var direction = e.Delta > 0 ? ZoomStep : -ZoomStep;
        ApplyZoom(Math.Clamp(_zoom + direction, MinimumZoom, MaximumZoom));
    }

    private void ApplyZoom(double zoom)
    {
        if (Math.Abs(zoom - _zoom) < 0.001)
        {
            return;
        }

        var oldCenter = new System.Windows.Point(Left + (Width / 2), Top + (Height / 2));
        _zoom = zoom;
        PublishSettings(_settings with { UiScale = _zoom });
        RootLayout.LayoutTransform = new ScaleTransform(_zoom, _zoom);

        var baseWidth = IsDocked ? DockedWidth : FullWidth;
        var baseHeight = IsDocked ? DockedHeight : FullHeight;
        Width = baseWidth * _zoom;
        Height = baseHeight * _zoom;

        if (IsDocked)
        {
            return;
        }

        Left = oldCenter.X - (Width / 2);
        Top = oldCenter.Y - (Height / 2);
        KeepInsideWorkingArea();
    }

    private void KeepInsideWorkingArea()
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var transformFromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = transformFromDevice.Transform(
            new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transformFromDevice.Transform(
            new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));

        Left = Math.Clamp(Left, topLeft.X, Math.Max(topLeft.X, bottomRight.X - Width));
        Top = Math.Clamp(Top, topLeft.Y, Math.Max(topLeft.Y, bottomRight.Y - Height));
    }
}
