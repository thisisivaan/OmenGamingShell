using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using Microsoft.Win32;

namespace OmenGamingShell.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            var deleteData = args.Any(a => a.Equals("/deletedata", StringComparison.OrdinalIgnoreCase));

            if (args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)))
            {
                SilentUninstaller.Run(deleteData);
                Shutdown();
                return;
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new UninstallerWindow().Show();
            return;
        }

        if (args.Any(a => a.Equals("/install", StringComparison.OrdinalIgnoreCase)))
        {
            var installPath = args.FirstOrDefault(a => a.StartsWith("/path=", StringComparison.OrdinalIgnoreCase))?.Substring(6);
            var shortcut = args.Any(a => a.Equals("/shortcut", StringComparison.OrdinalIgnoreCase));
            var shell = args.Any(a => a.Equals("/shell", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(installPath))
            {
                MessageBox.Show("Missing /path= argument.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            try
            {
                SilentInstaller.Run(installPath, shortcut, shell);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Installation failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }

        new InstallerWindow().Show();
    }
}

internal static class SilentInstaller
{
    public static void Run(string installPath, bool createShortcut, bool setAsShell)
    {
        Directory.CreateDirectory(installPath);

        var assembly = typeof(SilentInstaller).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("OmenGamingShell.zip", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException("Payload zip not found in installer resources.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var dest = Path.Combine(installPath, entry.Name);
            entry.ExtractToFile(dest, overwrite: true);
        }

        if (createShortcut)
        {
            var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var shortcutPath = Path.Combine(programsDir, "OMEN Gaming Shell.lnk");
            var exePath = Path.Combine(installPath, "OmenGamingShell.exe");
            var shellObj = (dynamic)Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell")!)!;
            var lnk = shellObj.CreateShortcut(shortcutPath);
            lnk.TargetPath = exePath;
            lnk.WorkingDirectory = installPath;
            lnk.Description = "OMEN Gaming Shell";
            lnk.Save();
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shellObj);
        }

        if (setAsShell)
        {
            var exePath = Path.Combine(installPath, "OmenGamingShell.exe");
            var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon");
            key?.SetValue("Shell", $"\"{exePath}\"");
            key?.Close();
        }
    }
}

internal static class SilentUninstaller
{
    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OmenGamingShell");
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell");

    public static void Run(bool deleteData)
    {
        try
        {
            var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon");
            key?.SetValue("Shell", "explorer.exe");
            key?.Close();
        }
        catch { }

        try
        {
            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "OMEN Gaming Shell.lnk");
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        }
        catch { }

        try
        {
            if (Directory.Exists(InstallDir))
                Directory.Delete(InstallDir, recursive: true);
        }
        catch { }

        if (deleteData)
        {
            try { if (Directory.Exists(AppDataDir)) Directory.Delete(AppDataDir, recursive: true); } catch { }
            try
            {
                using var credManager = Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c cmdkey /list | findstr OmenGamingShell && cmdkey /del:OmenGamingShell")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                credManager?.WaitForExit(3000);
            }
            catch { }
        }
    }
}
