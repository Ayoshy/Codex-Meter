using System.Threading;
using System.Windows;

namespace CodexUsageTray;

internal static class App
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (UpdateInstaller.TryRunApplyMode(args))
        {
            return;
        }
        UpdateInstaller.ScheduleCleanup(args);

        using var mutex = new Mutex(initiallyOwned: true, "Local\\CodexUsageTray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            var language = new AppSettingsService().Load().Language;
            System.Windows.MessageBox.Show(
                AppText.Get(language, TextId.AlreadyRunning),
                "Codex Meter",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        using var controller = new TrayApplicationController(application);
        controller.Start();
        application.Run();
    }
}
