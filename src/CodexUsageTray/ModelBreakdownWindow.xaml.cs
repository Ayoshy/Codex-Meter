using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using InputKey = System.Windows.Input.Key;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CodexUsageTray;

public partial class ModelBreakdownWindow : Window
{
    private const double BaseWidth = 790;
    private const double BaseHeight = 660;
    private const double MinimumZoom = 0.80;
    private const double MaximumZoom = 1.50;
    private const double ZoomStep = 0.10;

    private readonly ApiEquivalentEstimate _estimate;
    private readonly AppLanguage _language;
    private bool _ready;
    private bool _descending = true;
    private ModelBreakdownSort _sortMode = ModelBreakdownSort.Tokens;
    private double _zoom;

    public ModelBreakdownWindow(ApiEquivalentEstimate estimate, AppSettings settings)
    {
        InitializeComponent();
        _estimate = estimate;
        settings = AppSettingsService.Normalize(settings);
        _language = settings.Language;
        _zoom = settings.UiScale;
        ApplyWindowScale(_zoom);
        ApplyTexts();
        _ready = true;
        UpdateSortHeaders();
        ApplyRows();
    }

    public event Action<double>? UiScaleChanged;

    private void ApplyTexts()
    {
        var culture = AppText.Culture(_language);
        var title = AppText.Get(_language, TextId.ModelBreakdown);
        Title = $"Codex Meter — {title}";
        TitleText.Text = title;
        SubtitleText.Text = AppText.Get(_language, TextId.ModelBreakdownSubtitle);
        ObservedTokensLabel.Text = AppText.Get(_language, TextId.ObservedTokens);
        ApiCostLabel.Text = AppText.Get(_language, TextId.ApiCost);
        ModelsLabel.Text = AppText.Get(_language, TextId.Models);
        ModelHeader.Text = AppText.Get(_language, TextId.Model);
        EffortHeader.Text = AppText.Get(_language, TextId.Effort);
        TokensHeader.Text = AppText.Get(_language, TextId.Tokens).ToUpper(culture);
        SessionsHeader.Text = AppText.Get(_language, TextId.Sessions);
        CostHeader.Text = AppText.Get(_language, TextId.ApiCost);
        ShareHeader.Text = AppText.Get(_language, TextId.TokenShare);
        SearchHint.Text = AppText.Get(_language, TextId.SearchModels);

        var proxy = _estimate.UsesProxyPricing ? AppText.Get(_language, TextId.ProxyPricing) : string.Empty;
        var warning = _estimate.UnknownModels.Count == 0
            ? string.Empty
            : AppText.Format(_language, TextId.UnknownModels, string.Join(", ", _estimate.UnknownModels));
        FooterText.Text = AppText.Format(_language, TextId.EstimateDetails, _estimate.ParsedSessions) + proxy + warning;
    }

    private void ApplyRows()
    {
        if (!_ready)
        {
            return;
        }

        IEnumerable<ModelUsageBreakdown> filtered = _estimate.Models;
        var query = SearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(item => item.Model.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = ModelBreakdownOrdering.Sort(filtered, _sortMode, _descending).ToArray();
        RowsList.ItemsSource = ordered.Select(CreateRow).ToArray();
        UpdateSummary(ordered);
    }

    private void UpdateSummary(IReadOnlyCollection<ModelUsageBreakdown> rows)
    {
        var culture = AppText.Culture(_language);
        ObservedTokensValue.Text = UsageFormatter.CompactNumber(rows.Sum(item => item.TotalTokens), _language);
        var pricedRows = rows.Where(item => item.DollarAmount is not null).ToArray();
        ApiCostValue.Text = UsageFormatter.Dollars(
            pricedRows.Length == 0 ? null : pricedRows.Sum(item => item.DollarAmount ?? 0m),
            _language);
        ModelsValue.Text = rows.Select(item => item.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ToString("N0", culture);
        var sessions = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? _estimate.ParsedSessions
            : rows.Sum(item => item.Sessions);
        SessionsValue.Text = $"{sessions.ToString("N0", culture)} {AppText.Get(_language, TextId.Sessions).ToLower(culture)}";
    }

    private BreakdownRow CreateRow(ModelUsageBreakdown item)
    {
        var culture = AppText.Culture(_language);
        var effort = item.Effort.Equals("unspecified", StringComparison.OrdinalIgnoreCase)
            ? "—"
            : item.Effort.ToUpper(culture);
        var details = string.Join(
            " · ",
            $"{AppText.Get(_language, TextId.InputTokens)} {UsageFormatter.CompactNumber(item.InputTokens, _language)}",
            $"{AppText.Get(_language, TextId.CachedTokens)} {UsageFormatter.CompactNumber(item.CachedInputTokens, _language)}",
            $"{AppText.Get(_language, TextId.OutputTokens)} {UsageFormatter.CompactNumber(item.OutputTokens, _language)}");

        return new BreakdownRow(
            item.Model,
            effort,
            UsageFormatter.CompactNumber(item.TotalTokens, _language),
            details,
            item.Sessions.ToString("N0", culture),
            UsageFormatter.Dollars(item.DollarAmount, _language),
            item.TokenSharePercent.ToString("0.0", culture) + "%",
            item.TokenSharePercent);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplyRows();
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<ModelBreakdownSort>(tag, out var mode))
        {
            return;
        }

        if (_sortMode == mode)
        {
            _descending = !_descending;
        }
        else
        {
            _sortMode = mode;
            _descending = mode is not ModelBreakdownSort.Model;
        }

        UpdateSortHeaders();
        ApplyRows();
    }

    private void UpdateSortHeaders()
    {
        ModelSortIndicator.Text = SortIndicator(ModelBreakdownSort.Model);
        EffortSortIndicator.Text = SortIndicator(ModelBreakdownSort.Effort);
        TokensSortIndicator.Text = SortIndicator(ModelBreakdownSort.Tokens);
        SessionsSortIndicator.Text = SortIndicator(ModelBreakdownSort.Sessions);
        CostSortIndicator.Text = SortIndicator(ModelBreakdownSort.Cost);
        ShareSortIndicator.Text = SortIndicator(ModelBreakdownSort.Share);
    }

    private string SortIndicator(ModelBreakdownSort mode) => _sortMode == mode
        ? _descending ? "↓" : "↑"
        : string.Empty;

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            return;
        }

        e.Handled = true;
        var direction = e.Delta > 0 ? ZoomStep : -ZoomStep;
        var zoom = Math.Round(Math.Clamp(_zoom + direction, MinimumZoom, MaximumZoom), 2);
        if (Math.Abs(zoom - _zoom) < 0.001)
        {
            return;
        }

        _zoom = zoom;
        ApplyWindowScale(_zoom);
        UiScaleChanged?.Invoke(_zoom);
    }

    private void ApplyWindowScale(double zoom)
    {
        var oldCenter = IsLoaded ? new System.Windows.Point(Left + (Width / 2), Top + (Height / 2)) : default;
        RootLayout.LayoutTransform = new ScaleTransform(zoom, zoom);
        Width = BaseWidth * zoom;
        Height = BaseHeight * zoom;
        if (IsLoaded)
        {
            Left = oldCenter.X - (Width / 2);
            Top = oldCenter.Y - (Height / 2);
        }
    }

    private void WindowFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement frame)
        {
            frame.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 22, 22);
        }
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
        if (e.Key == InputKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private sealed record BreakdownRow(
        string Model,
        string Effort,
        string Tokens,
        string TokenDetails,
        string Sessions,
        string Cost,
        string Share,
        double ShareValue);
}