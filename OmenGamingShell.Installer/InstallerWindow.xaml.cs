using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace OmenGamingShell.Installer;

public partial class InstallerWindow : Window
{
    private string _installPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OmenGamingShell");

    public InstallerWindow()
    {
        InitializeComponent();
        InstallPathText.Text = _installPath;
    }

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private void WindowDrag_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        ShowStep(Step2_Location);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ShowStep(Step1_Welcome);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose Install Location" };
        if (dialog.ShowDialog(this) == true)
        {
            _installPath = Path.Combine(dialog.FolderName, "OmenGamingShell");
            InstallPathText.Text = _installPath;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var createShortcut = CreateShortcutCheck.IsChecked == true;
        var setAsShell = SetAsShellCheck.IsChecked == true;

        ShowStep(Step3_Installing);
        InstallStatusText.Text = "Requesting administrator privileges...";
        InstallProgress.Value = 10;

        try
        {
            var args = $"/install /path=\"{_installPath}\"";
            if (createShortcut) args += " /shortcut";
            if (setAsShell) args += " /shell";

            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync();
            }

            InstallStatusText.Text = "Finalizing...";
            InstallProgress.Value = 100;

            var shellNote = setAsShell ? "Shell registered." : "";
            var shortcutNote = createShortcut ? "Start Menu shortcut created." : "";
            CompleteSummary.Text = $"Installed to:\n{_installPath}\n\n{shellNote} {shortcutNote}".Trim();
            ShowStep(Step4_Complete);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            InstallStatusText.Text = "UAC denied. Installation cancelled.";
            InstallProgress.Value = 0;
            ShowStep(Step2_Location);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installation failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ShowStep(Step2_Location);
        }
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchOnFinishCheck.IsChecked == true)
        {
            var exePath = Path.Combine(_installPath, "OmenGamingShell.exe");
            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            }
        }
        Application.Current.Shutdown();
    }

    private void ShowStep(FrameworkElement step)
    {
        Step1_Welcome.Visibility = Visibility.Collapsed;
        Step2_Location.Visibility = Visibility.Collapsed;
        Step3_Installing.Visibility = Visibility.Collapsed;
        Step4_Complete.Visibility = Visibility.Collapsed;
        step.Visibility = Visibility.Visible;
    }
}
