using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using InputKey = System.Windows.Input.Key;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CodexUsageTray;

public partial class SettingsWindow : Window
{
    private AppSettings _settings;
    private bool _applying;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = AppSettingsService.Normalize(settings);

        LanguageCombo.SelectionChanged += (_, _) => UpdateFromControls();
        IntervalCombo.SelectionChanged += (_, _) => UpdateFromControls();
        StartupCombo.SelectionChanged += (_, _) => UpdateFromControls();
        ScaleCombo.SelectionChanged += (_, _) => UpdateFromControls();
        DockCornerCombo.SelectionChanged += (_, _) => UpdateFromControls();
        AlwaysOnTopToggle.Checked += (_, _) => UpdateFromControls();
        AlwaysOnTopToggle.Unchecked += (_, _) => UpdateFromControls();
        StartWithWindowsToggle.Checked += (_, _) => UpdateFromControls();
        StartWithWindowsToggle.Unchecked += (_, _) => UpdateFromControls();
        ApiEstimateToggle.Checked += (_, _) => UpdateFromControls();
        ApiEstimateToggle.Unchecked += (_, _) => UpdateFromControls();
        ClearCacheButton.Click += (_, _) => ShowClearCacheConfirmation();
        CancelClearButton.Click += (_, _) => HideClearCacheConfirmation();
        ConfirmClearButton.Click += (_, _) => ConfirmClearCache();
        ResetSettingsButton.Click += (_, _) => ResetSettingsRequested?.Invoke(this, EventArgs.Empty);

        ApplySettings(_settings);
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

    public event EventHandler<AppSettings>? SettingsChanged;
    public event EventHandler? ClearCacheRequested;
    public event EventHandler? ResetSettingsRequested;

    public void ApplySettings(AppSettings settings)
    {
        _settings = AppSettingsService.Normalize(settings);
        _applying = true;
        try
        {
            ApplyTexts();
            PopulateOptions();
            Select(LanguageCombo, _settings.Language);
            Select(IntervalCombo, _settings.RefreshIntervalMinutes);
            Select(StartupCombo, _settings.StartupBehavior);
            Select(ScaleCombo, _settings.UiScale);
            Select(DockCornerCombo, _settings.CompactDockCorner);
            AlwaysOnTopToggle.IsChecked = _settings.CompactAlwaysOnTop;
            StartWithWindowsToggle.IsChecked = _settings.StartWithWindows;
            ApiEstimateToggle.IsChecked = _settings.ApiEquivalentEnabled;
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyTexts()
    {
        var language = _settings.Language;
        var options = AppText.Get(language, TextId.Options);
        Title = $"Codex Meter \u2014 {options}";
        OptionsTitleText.Text = options;
        OptionsHeaderText.Text = options.ToUpper(AppText.Culture(language));
        SettingsSubtitleText.Text = AppText.Get(language, TextId.SettingsSubtitle);
        GeneralHeading.Text = AppText.Get(language, TextId.General);
        LanguageLabel.Text = AppText.Get(language, TextId.Language);
        IntervalLabel.Text = AppText.Get(language, TextId.RefreshInterval);
        StartupLabel.Text = AppText.Get(language, TextId.StartupBehavior);
        ScaleLabel.Text = AppText.Get(language, TextId.UiScale);
        DockCornerLabel.Text = AppText.Get(language, TextId.DockPosition);
        AlwaysOnTopToggle.Content = AppText.Get(language, TextId.CompactAlwaysOnTop);
        StartWithWindowsToggle.Content = AppText.Get(language, TextId.StartWithWindows);
        ApiEstimateToggle.Content = AppText.Get(language, TextId.ApiEquivalent);
        EstimateDescription.Text = AppText.Get(language, TextId.ApiEquivalentDescription);
        ClearCacheButton.Content = AppText.Get(language, TextId.ClearLocalCache);
        CacheDescription.Text = AppText.Get(language, TextId.ClearCacheDescription);
        ResetSettingsButton.Content = AppText.Get(language, TextId.ResetSettings);
        ConfirmationTitleText.Text = AppText.Get(language, TextId.ClearCacheTitle);
        ConfirmationMessageText.Text = AppText.Get(language, TextId.ClearCacheConfirmation);
        CancelClearButton.Content = AppText.Get(language, TextId.Cancel);
        ConfirmClearButton.Content = AppText.Get(language, TextId.ClearCacheTitle);
    }

    private void PopulateOptions()
    {
        var language = _settings.Language;
        SetItems(LanguageCombo,
        [
            new Option<AppLanguage>(AppLanguage.English, AppText.Get(language, TextId.English)),
            new Option<AppLanguage>(AppLanguage.French, AppText.Get(language, TextId.French))
        ]);
        SetItems(IntervalCombo,
        [
            new Option<int>(1, AppText.Get(language, TextId.OneMinute)),
            new Option<int>(5, AppText.Get(language, TextId.FiveMinutes)),
            new Option<int>(15, AppText.Get(language, TextId.FifteenMinutes))
        ]);
        SetItems(StartupCombo,
        [
            new Option<StartupBehavior>(StartupBehavior.FullWindow, AppText.Get(language, TextId.FullWindow)),
            new Option<StartupBehavior>(StartupBehavior.CompactWidget, AppText.Get(language, TextId.CompactWidget)),
            new Option<StartupBehavior>(StartupBehavior.TrayOnly, AppText.Get(language, TextId.TrayOnly)),
            new Option<StartupBehavior>(StartupBehavior.LastUsedView, AppText.Get(language, TextId.LastUsedView))
        ]);
        SetItems(ScaleCombo,
        [
            new Option<double>(0.80d, "80%"),
            new Option<double>(0.90d, "90%"),
            new Option<double>(1.00d, "100%"),
            new Option<double>(1.10d, "110%"),
            new Option<double>(1.20d, "120%"),
            new Option<double>(1.30d, "130%"),
            new Option<double>(1.40d, "140%"),
            new Option<double>(1.50d, "150%")
        ]);
        SetItems(DockCornerCombo,
        [
            new Option<DockCorner>(DockCorner.TopLeft, AppText.Get(language, TextId.TopLeft)),
            new Option<DockCorner>(DockCorner.TopRight, AppText.Get(language, TextId.TopRight)),
            new Option<DockCorner>(DockCorner.BottomLeft, AppText.Get(language, TextId.BottomLeft)),
            new Option<DockCorner>(DockCorner.BottomRight, AppText.Get(language, TextId.BottomRight))
        ]);
    }

    private void UpdateFromControls()
    {
        if (_applying ||
            Selected<AppLanguage>(LanguageCombo) is not { } language ||
            Selected<int>(IntervalCombo) is not { } interval ||
            Selected<StartupBehavior>(StartupCombo) is not { } startup ||
            Selected<double>(ScaleCombo) is not { } scale ||
            Selected<DockCorner>(DockCornerCombo) is not { } dockCorner)
        {
            return;
        }

        var updated = AppSettingsService.Normalize(_settings with
        {
            Language = language,
            RefreshIntervalMinutes = interval,
            StartupBehavior = startup,
            UiScale = scale,
            CompactAlwaysOnTop = AlwaysOnTopToggle.IsChecked == true,
            CompactDockCorner = dockCorner,
            StartWithWindows = StartWithWindowsToggle.IsChecked == true,
            ApiEquivalentEnabled = ApiEstimateToggle.IsChecked == true
        });
        if (updated == _settings)
        {
            return;
        }

        ApplySettings(updated);
        SettingsChanged?.Invoke(this, updated);
    }

    private void ShowClearCacheConfirmation()
    {
        ConfirmationOverlay.Visibility = Visibility.Visible;
        CancelClearButton.Focus();
    }

    private void HideClearCacheConfirmation()
    {
        ConfirmationOverlay.Visibility = Visibility.Collapsed;
        ClearCacheButton.Focus();
    }

    private void ConfirmClearCache()
    {
        HideClearCacheConfirmation();
        ClearCacheRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            e.OriginalSource is DependencyObject source && HasButtonAncestor(source))
        {
            return;
        }

        DragMove();
    }

    private static bool HasButtonAncestor(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, InputKeyEventArgs e)
    {
        if (e.Key != InputKey.Escape)
        {
            return;
        }

        if (ConfirmationOverlay.Visibility == Visibility.Visible)
        {
            HideClearCacheConfirmation();
        }
        else
        {
            Close();
        }

        e.Handled = true;
    }

    private static void SetItems<T>(ComboBox comboBox, IEnumerable<Option<T>> options)
    {
        comboBox.Items.Clear();
        foreach (var option in options)
        {
            comboBox.Items.Add(option);
        }
    }

    private static void Select<T>(ComboBox comboBox, T value)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<Option<T>>()
            .FirstOrDefault(option => EqualityComparer<T>.Default.Equals(option.Value, value));
    }

    private static T? Selected<T>(ComboBox comboBox) where T : struct =>
        comboBox.SelectedItem is Option<T> option ? option.Value : null;

    private sealed record Option<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}
