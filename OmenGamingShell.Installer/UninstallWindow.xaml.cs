using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;

namespace OmenGamingShell.Installer;

public partial class UninstallWindow : Window
{
    private readonly string _installFolder = Path.GetFullPath(AppContext.BaseDirectory)
        .TrimEnd(Path.DirectorySeparatorChar);

    public UninstallWindow() => InitializeComponent();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        UninstallButton.IsEnabled = false;
        StatusText.Text = "REMOVING OMEN GAMING SHELL";
        try
        {
            ValidateInstallFolder();
            await Task.Run(RemoveRegisteredInstallation);
            StatusText.Text = "UNINSTALL COMPLETE";
            ScheduleInstallFolderRemoval();
            await Task.Delay(450);
            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"UNINSTALL FAILED: {exception.Message.ToUpperInvariant()}";
            UninstallButton.IsEnabled = true;
        }
    }

    private void ValidateInstallFolder()
    {
        var root = Path.GetPathRoot(_installFolder);
        if (string.IsNullOrWhiteSpace(root) ||
            string.Equals(_installFolder, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(_installFolder, "OmenGamingShell.exe")))
            throw new InvalidOperationException("The installation folder could not be verified.");
    }

    private void RemoveRegisteredInstallation()
    {
        foreach (var process in Process.GetProcessesByName("OmenGamingShell"))
        {
            try
            {
                if (process.MainModule?.FileName.StartsWith(_installFolder, StringComparison.OrdinalIgnoreCase) == true)
                    process.Kill(true);
            }
            catch { }
            finally { process.Dispose(); }
        }

        DeleteIfPresent(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OMEN Gaming Shell.lnk"));
        var startFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "OMEN Gaming Shell");
        if (Directory.Exists(startFolder)) Directory.Delete(startFolder, true);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OmenGamingShell", false);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private void ScheduleInstallFolderRemoval()
    {
        var escapedFolder = _installFolder.Replace("'", "''");
        var command = $"Wait-Process -Id {Environment.ProcessId}; Remove-Item -LiteralPath '{escapedFolder}' -Recurse -Force";
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{command}\""
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
