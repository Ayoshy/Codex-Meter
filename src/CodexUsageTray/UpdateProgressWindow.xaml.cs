using System.Windows;
using System.Windows.Media;

namespace CodexUsageTray;

public partial class UpdateProgressWindow : Window
{
    private const double BaseWidth = 440;
    private const double BaseHeight = 250;
    private readonly AppLanguage _language;

    public UpdateProgressWindow(AppLanguage language, AppVersion version, double uiScale)
    {
        InitializeComponent();
        _language = language;
        TitleText.Text = AppText.Format(language, TextId.UpdateProgressTitle, version);
        ApplyScale(uiScale);
        Report(new UpdateProgress(UpdatePhase.Downloading));
    }

    public void Report(UpdateProgress progress)
    {
        StatusText.Text = AppText.Get(
            _language,
            progress.Phase switch
            {
                UpdatePhase.Verifying => TextId.UpdateVerifying,
                UpdatePhase.Preparing => TextId.UpdatePreparing,
                _ => TextId.UpdateDownloading
            });

        if (progress.Phase != UpdatePhase.Downloading ||
            progress.TotalBytes is not > 0)
        {
            DownloadProgress.IsIndeterminate = true;
            DetailText.Text = string.Empty;
            return;
        }

        DownloadProgress.IsIndeterminate = false;
        var percentage = Math.Clamp(
            progress.BytesReceived / (double)progress.TotalBytes.Value * 100d,
            0d,
            100d);
        DownloadProgress.Value = percentage;
        DetailText.Text = $"{percentage:0}%";
    }

    private void ApplyScale(double uiScale)
    {
        var scale = Math.Round(Math.Clamp(uiScale, 0.80d, 1.50d), 2);
        RootLayout.LayoutTransform = new ScaleTransform(scale, scale);
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
    }
}
