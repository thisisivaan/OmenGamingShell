using System.Windows;

namespace OmenGamingShell.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = e.Args.Any(argument => argument.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            ? new UninstallWindow()
            : new MainWindow();
        MainWindow.Show();
    }
}
