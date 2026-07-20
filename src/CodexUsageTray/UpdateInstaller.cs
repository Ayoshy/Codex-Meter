using System.Diagnostics;
using System.IO;

namespace CodexUsageTray;

public static class UpdateInstaller
{
    private const string ApplyArgument = "--apply-update";
    private const string CleanupArgument = "--cleanup-update";

    public static bool TryRunApplyMode(string[] args)
    {
        if (args.Length != 4 ||
            !args[0].Equals(ApplyArgument, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(args[2], out var processId))
        {
            return false;
        }

        Environment.ExitCode = Apply(args[1], processId, args[3]);
        return true;
    }

    public static void ScheduleCleanup(string[] args)
    {
        if (args.Length != 2 ||
            !args[0].Equals(CleanupArgument, StringComparison.OrdinalIgnoreCase) ||
            !IsSafeStagingDirectory(args[1]))
        {
            return;
        }

        var stagingDirectory = args[1];
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                try
                {
                    if (Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory, recursive: true);
                    }
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The helper may still be exiting. Retry briefly in the background.
                }
            }
        });
    }

    public static ProcessStartInfo CreateApplyStartInfo(
        StagedUpdate update,
        string targetExecutable,
        int processId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = update.ExecutablePath,
            WorkingDirectory = update.StagingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(ApplyArgument);
        startInfo.ArgumentList.Add(Path.GetFullPath(targetExecutable));
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(Path.GetFullPath(update.StagingDirectory));
        return startInfo;
    }

    public static void Launch(StagedUpdate update)
    {
        var targetExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(targetExecutable) ||
            !Path.GetFileName(targetExecutable).Equals("CodexMeter.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic update requires CodexMeter.exe to be launched directly.");
        }

        _ = Process.Start(CreateApplyStartInfo(update, targetExecutable, Environment.ProcessId))
            ?? throw new InvalidOperationException("Could not start the update helper.");
    }

    private static int Apply(string targetExecutable, int processId, string stagingDirectory)
    {
        var target = Path.GetFullPath(targetExecutable);
        var staging = Path.GetFullPath(stagingDirectory);
        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(source) ||
            !Path.GetDirectoryName(Path.GetFullPath(source))!
                .Equals(staging, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).Equals("CodexMeter.exe", StringComparison.OrdinalIgnoreCase) ||
            !IsSafeStagingDirectory(staging))
        {
            return 2;
        }

        try
        {
            WaitForExit(processId);
            var targetDirectory = Path.GetDirectoryName(target)
                ?? throw new InvalidOperationException("The installation directory is unavailable.");
            var backupPath = ReplaceExecutable(source, target);
            try
            {
                var restart = new ProcessStartInfo
                {
                    FileName = target,
                    WorkingDirectory = targetDirectory,
                    UseShellExecute = true
                };
                restart.ArgumentList.Add(CleanupArgument);
                restart.ArgumentList.Add(staging);
                _ = Process.Start(restart)
                    ?? throw new InvalidOperationException("The updated application could not be restarted.");
                TryDeleteFile(backupPath);
                return 0;
            }
            catch
            {
                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, target, overwrite: true);
                }
                throw;
            }
        }
        catch (Exception exception)
        {
            WriteFailureLog(exception);
            System.Windows.MessageBox.Show(
                "The update could not be installed. Codex Meter was not changed.",
                "Codex Meter",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }

    internal static string ReplaceExecutable(string sourceExecutable, string targetExecutable)
    {
        var source = Path.GetFullPath(sourceExecutable);
        var target = Path.GetFullPath(targetExecutable);
        var targetDirectory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("The installation directory is unavailable.");
        var temporaryTarget = Path.Combine(
            targetDirectory,
            $".CodexMeter.update-{Guid.NewGuid():N}.exe");
        var backupPath = target + ".update-backup";

        File.Copy(source, temporaryTarget, overwrite: false);
        TryDeleteFile(backupPath);
        try
        {
            if (File.Exists(target))
            {
                File.Replace(temporaryTarget, target, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryTarget, target);
            }
            return backupPath;
        }
        finally
        {
            TryDeleteFile(temporaryTarget);
        }
    }

    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            {
                throw new TimeoutException("Codex Meter did not close in time.");
            }
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
    }

    private static bool IsSafeStagingDirectory(string path)
    {
        var root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexMeter",
            "updates")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Non-critical cleanup.
        }
    }

    private static void WriteFailureLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexMeter");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "update-error.log"),
                $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            // The error dialog remains available even if logging fails.
        }
    }
}
