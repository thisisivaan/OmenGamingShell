using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += HandleCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash(args.ExceptionObject as Exception ?? new Exception("Unknown fatal error"));
        base.OnStartup(e);
        EnsureFirewallRule();
        System.Windows.Window shell = SetupStateStore.IsSetupComplete() ? new MainWindow() : new SetupWindow();
        MainWindow = shell;
        shell.Show();
    }

    private static void EnsureFirewallRule()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall add rule name=\"OmenGamingShell\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            Process.Start(psi)?.WaitForExit(2000);

            psi = new ProcessStartInfo("netsh", $"advfirewall firewall add rule name=\"OmenGamingShell Out\" dir=out action=allow program=\"{exePath}\" enable=yes profile=any")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            Process.Start(psi)?.WaitForExit(2000);
        }
        catch { }
    }

    private void HandleCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
                e.Handled = true;
    }

    private static void WriteCrash(Exception exception)
    {
        try
        {
            ErrorLogStore.Log(exception.Message, "Unhandled exception", exception.ToString());
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OmenGamingShell", "Logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crashes.log"),
                $"[{DateTime.Now:O}] {exception}\n\n");
        }
        catch { }
    }
}



