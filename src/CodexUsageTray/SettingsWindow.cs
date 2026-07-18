using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;

namespace CodexUsageTray;

public sealed class SettingsWindow : Window
{
    private readonly ComboBox _language = CreateComboBox();
    private readonly ComboBox _interval = CreateComboBox();
    private readonly ComboBox _startup = CreateComboBox();
    private readonly ComboBox _scale = CreateComboBox();
    private readonly CheckBox _alwaysOnTop = CreateCheckBox();
    private readonly CheckBox _apiEstimate = CreateCheckBox();
    private readonly Button _clearCache = CreateButton();
    private readonly TextBlock _generalHeading = CreateHeading();
    private readonly TextBlock _languageLabel = CreateLabel();
    private readonly TextBlock _intervalLabel = CreateLabel();
    private readonly TextBlock _startupLabel = CreateLabel();
    private readonly TextBlock _scaleLabel = CreateLabel();
    private readonly TextBlock _estimateDescription = CreateDescription();
    private readonly TextBlock _cacheDescription = CreateDescription();
    private AppSettings _settings;
    private bool _applying;

    public SettingsWindow(AppSettings settings)
    {
        _settings = AppSettingsService.Normalize(settings);
        Title = "Codex Meter";
        Width = 440;
        Height = 610;
        MinWidth = 390;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BrushFrom("#0C1016");
        Foreground = BrushFrom("#F7F8FA");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var content = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        content.Children.Add(new TextBlock
        {
            Text = "Codex Meter",
            FontWeight = FontWeights.SemiBold,
            FontSize = 18,
            Foreground = BrushFrom("#F7F8FA")
        });
        content.Children.Add(_generalHeading);
        content.Children.Add(CreateRow(_languageLabel, _language));
        content.Children.Add(CreateRow(_intervalLabel, _interval));
        content.Children.Add(CreateRow(_startupLabel, _startup));
        content.Children.Add(CreateRow(_scaleLabel, _scale));
        content.Children.Add(_alwaysOnTop);
        content.Children.Add(new Border { Height = 1, Background = BrushFrom("#293240"), Margin = new Thickness(0, 18, 0, 16) });
        content.Children.Add(_apiEstimate);
        content.Children.Add(_estimateDescription);
        content.Children.Add(new Border { Height = 1, Background = BrushFrom("#293240"), Margin = new Thickness(0, 18, 0, 16) });
        content.Children.Add(_clearCache);
        content.Children.Add(_cacheDescription);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border
            {
                Margin = new Thickness(14),
                Background = BrushFrom("#F5101620"),
                BorderBrush = BrushFrom("#34404F"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Child = content
            }
        };

        _language.SelectionChanged += (_, _) => UpdateFromControls();
        _interval.SelectionChanged += (_, _) => UpdateFromControls();
        _startup.SelectionChanged += (_, _) => UpdateFromControls();
        _scale.SelectionChanged += (_, _) => UpdateFromControls();
        _alwaysOnTop.Checked += (_, _) => UpdateFromControls();
        _alwaysOnTop.Unchecked += (_, _) => UpdateFromControls();
        _apiEstimate.Checked += (_, _) => UpdateFromControls();
        _apiEstimate.Unchecked += (_, _) => UpdateFromControls();
        _clearCache.Click += (_, _) => ConfirmClearCache();
        ApplySettings(_settings);
    }

    public event EventHandler<AppSettings>? SettingsChanged;
    public event EventHandler? ClearCacheRequested;

    public void ApplySettings(AppSettings settings)
    {
        _settings = AppSettingsService.Normalize(settings);
        _applying = true;
        try
        {
            ApplyTexts();
            PopulateOptions();
            Select(_language, _settings.Language);
            Select(_interval, _settings.RefreshIntervalMinutes);
            Select(_startup, _settings.StartupBehavior);
            Select(_scale, _settings.UiScale);
            _alwaysOnTop.IsChecked = _settings.CompactAlwaysOnTop;
            _apiEstimate.IsChecked = _settings.ApiEquivalentEnabled;
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyTexts()
    {
        var language = _settings.Language;
        Title = $"Codex Meter — {AppText.Get(language, TextId.Options)}";
        _generalHeading.Text = AppText.Get(language, TextId.General);
        _languageLabel.Text = AppText.Get(language, TextId.Language);
        _intervalLabel.Text = AppText.Get(language, TextId.RefreshInterval);
        _startupLabel.Text = AppText.Get(language, TextId.StartupBehavior);
        _scaleLabel.Text = AppText.Get(language, TextId.UiScale);
        _alwaysOnTop.Content = AppText.Get(language, TextId.CompactAlwaysOnTop);
        _apiEstimate.Content = AppText.Get(language, TextId.ApiEquivalent);
        _estimateDescription.Text = AppText.Get(language, TextId.ApiEquivalentDescription);
        _clearCache.Content = AppText.Get(language, TextId.ClearLocalCache);
        _cacheDescription.Text = AppText.Get(language, TextId.ClearCacheDescription);
    }

    private void PopulateOptions()
    {
        var language = _settings.Language;
        SetItems(_language,
        [
            new Option<AppLanguage>(AppLanguage.English, AppText.Get(language, TextId.English)),
            new Option<AppLanguage>(AppLanguage.French, AppText.Get(language, TextId.French))
        ]);
        SetItems(_interval,
        [
            new Option<int>(1, AppText.Get(language, TextId.OneMinute)),
            new Option<int>(5, AppText.Get(language, TextId.FiveMinutes)),
            new Option<int>(15, AppText.Get(language, TextId.FifteenMinutes))
        ]);
        SetItems(_startup,
        [
            new Option<StartupBehavior>(StartupBehavior.FullWindow, AppText.Get(language, TextId.FullWindow)),
            new Option<StartupBehavior>(StartupBehavior.CompactWidget, AppText.Get(language, TextId.CompactWidget)),
            new Option<StartupBehavior>(StartupBehavior.TrayOnly, AppText.Get(language, TextId.TrayOnly)),
            new Option<StartupBehavior>(StartupBehavior.LastUsedView, AppText.Get(language, TextId.LastUsedView))
        ]);
        SetItems(_scale,
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
    }

    private void UpdateFromControls()
    {
        if (_applying ||
            Selected<AppLanguage>(_language) is not { } language ||
            Selected<int>(_interval) is not { } interval ||
            Selected<StartupBehavior>(_startup) is not { } startup ||
            Selected<double>(_scale) is not { } scale)
        {
            return;
        }

        var updated = AppSettingsService.Normalize(_settings with
        {
            Language = language,
            RefreshIntervalMinutes = interval,
            StartupBehavior = startup,
            UiScale = scale,
            CompactAlwaysOnTop = _alwaysOnTop.IsChecked == true,
            ApiEquivalentEnabled = _apiEstimate.IsChecked == true
        });
        if (updated == _settings)
        {
            return;
        }

        ApplySettings(updated);
        SettingsChanged?.Invoke(this, updated);
    }

    private void ConfirmClearCache()
    {
        var language = _settings.Language;
        if (MessageBox.Show(
            AppText.Get(language, TextId.ClearCacheConfirmation),
            AppText.Get(language, TextId.ClearCacheTitle),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            ClearCacheRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static StackPanel CreateRow(TextBlock label, ComboBox value)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        row.Children.Add(label);
        row.Children.Add(value);
        return row;
    }

    private static ComboBox CreateComboBox() => new()
    {
        Height = 34,
        Margin = new Thickness(0, 6, 0, 0),
        Background = BrushFrom("#1B232E"),
        Foreground = BrushFrom("#F7F8FA"),
        BorderBrush = BrushFrom("#405267"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(9, 2, 9, 2)
    };

    private static CheckBox CreateCheckBox() => new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        Foreground = BrushFrom("#E8EDF3"),
        FontWeight = FontWeights.SemiBold
    };

    private static Button CreateButton() => new()
    {
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        Padding = new Thickness(14, 8, 14, 8),
        Background = BrushFrom("#1B232E"),
        Foreground = BrushFrom("#E8EDF3"),
        BorderBrush = BrushFrom("#405267"),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand,
        FontWeight = FontWeights.SemiBold
    };

    private static TextBlock CreateHeading() => new()
    {
        Margin = new Thickness(0, 20, 0, 16),
        Foreground = BrushFrom("#55E6A5"),
        FontSize = 10,
        FontWeight = FontWeights.Bold
    };

    private static TextBlock CreateLabel() => new()
    {
        Foreground = BrushFrom("#B8C2CE"),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold
    };

    private static TextBlock CreateDescription() => new()
    {
        Margin = new Thickness(0, 5, 0, 0),
        Foreground = BrushFrom("#8C97A8"),
        FontSize = 10.5,
        TextWrapping = TextWrapping.Wrap
    };

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

    private static SolidColorBrush BrushFrom(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private sealed record Option<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}
