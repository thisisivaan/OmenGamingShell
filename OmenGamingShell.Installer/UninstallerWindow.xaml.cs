using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OmenGamingShell.Installer;

public partial class UninstallerWindow : Window
{
    public UninstallerWindow()
    {
        InitializeComponent();
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        Step1_Options.Visibility = Visibility.Collapsed;
        Step2_Progress.Visibility = Visibility.Visible;

        var deleteData = DeleteDataCheck.IsChecked == true;

        try
        {
            UninstallStatusText.Text = "Requesting administrator privileges...";
            UninstallProgress.Value = 10;

            var args = "/uninstall /silent";
            if (deleteData) args += " /deletedata";

            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc is not null)
                await proc.WaitForExitAsync();

            UninstallProgress.Value = 100;
            UninstallTitle.Text = "UNINSTALL COMPLETE";
            UninstallTitle.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            UninstallStatusText.Visibility = Visibility.Collapsed;
            UninstallProgress.Visibility = Visibility.Collapsed;
            UninstallDoneText.Text = "OMEN Gaming Shell has been removed from your system.\nWindows Explorer is restored as your default shell.";
            UninstallDoneText.Visibility = Visibility.Visible;
            DoneButton.Visibility = Visibility.Visible;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            UninstallStatusText.Text = "UAC denied. Uninstall cancelled.";
            UninstallProgress.Value = 0;
            Step1_Options.Visibility = Visibility.Visible;
            Step2_Progress.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            UninstallTitle.Text = "UNINSTALL FAILED";
            UninstallTitle.Foreground = System.Windows.Media.Brushes.Red;
            UninstallStatusText.Text = ex.Message;
            UninstallProgress.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Visible;
        }
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
