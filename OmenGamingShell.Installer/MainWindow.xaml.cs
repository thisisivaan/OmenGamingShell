using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;

namespace OmenGamingShell.Installer;

public partial class MainWindow : Window
{
    private string _installFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "OMEN Gaming Shell");

    public MainWindow()
    {
        InitializeComponent();
        InstallPathText.Text = _installFolder;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        try
        {
            await Task.Run(InstallFiles);
            InstallProgress.Value = 72;
            InstallUninstaller();
            CreateShortcuts();
            RegisterInstallation();
            InstallProgress.Value = 100;
            StatusText.Text = "INSTALLATION COMPLETE";
            InstallButton.Content = "LAUNCH";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= Install_Click;
            InstallButton.Click += Launch_Click;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"INSTALLATION FAILED: {exception.Message.ToUpperInvariant()}";
            InstallButton.IsEnabled = true;
        }
    }

    private void ChooseInstallPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to install OMEN Gaming Shell",
            InitialDirectory = Directory.Exists(_installFolder) ? _installFolder : Path.GetDirectoryName(_installFolder),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        _installFolder = dialog.FolderName;
        InstallPathText.Text = _installFolder;
    }

    private void InstallFiles()
    {
        Dispatcher.Invoke(() => { StatusText.Text = "INSTALLING FILES"; InstallProgress.Value = 18; });
        Directory.CreateDirectory(_installFolder);
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Payload/OmenGamingShell.zip"))
                       ?? throw new InvalidOperationException("Installer payload is missing.");
        using (resource.Stream) ZipFile.ExtractToDirectory(resource.Stream, _installFolder, true);
        Dispatcher.Invoke(() => InstallProgress.Value = 65);
    }

    private void CreateShortcuts()
    {
        var executable = Path.Combine(_installFolder, "OmenGamingShell.exe");
        if (DesktopShortcutCheck.IsChecked == true)
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OMEN Gaming Shell.lnk"), executable);
        if (StartMenuShortcutCheck.IsChecked == true)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "OMEN Gaming Shell");
            Directory.CreateDirectory(folder);
            CreateShortcut(Path.Combine(folder, "OMEN Gaming Shell.lnk"), executable);
        }
    }

    private void InstallUninstaller()
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Installer path is unavailable.");
        File.Copy(source, Path.Combine(_installFolder, "Uninstall OMEN Gaming Shell.exe"), true);
    }

    private static void CreateShortcut(string shortcutPath, string executable)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut service is unavailable.");
        var shell = Activator.CreateInstance(shellType)!;
        var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
        try
        {
            var type = shortcut!.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { executable });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(executable)! });
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { $"{executable},0" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private void RegisterInstallation()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OmenGamingShell");
        key.SetValue("DisplayName", "OMEN Gaming Shell");
        key.SetValue("DisplayVersion", "1.0.0");
        key.SetValue("Publisher", "OMEN Gaming Shell");
        key.SetValue("DisplayIcon", Path.Combine(_installFolder, "OmenGamingShell.exe"));
        key.SetValue("InstallLocation", _installFolder);
        key.SetValue("UninstallString", $"\"{Path.Combine(_installFolder, "Uninstall OMEN Gaming Shell.exe")}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{Path.Combine(_installFolder, "Uninstall OMEN Gaming Shell.exe")}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(_installFolder, "OmenGamingShell.exe")) { UseShellExecute = true });
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
