using System.Threading;
using System.Windows;

namespace CodexUsageTray;

internal static class App
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "Local\\CodexUsageTray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "Codex Meter est déjà lancé.",
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
