using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    private OmenKeyWatcher? _omenKey;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += HandleCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash(args.ExceptionObject as Exception ?? new Exception("Unknown fatal error"));
        base.OnStartup(e);

        _instance = new SingleInstance();
        if (!_instance.IsPrimary)
        {
            SingleInstance.SignalPrimary();
            Shutdown();
            return;
        }

        EnsureFirewallRule();
        System.Windows.Window shell = SetupStateStore.IsSetupComplete() ? new MainWindow() : new SetupWindow();
        MainWindow = shell;
        shell.Show();

        _instance.Listen(RaiseShell);
        _omenKey = new OmenKeyWatcher();
        _omenKey.TryStart(OnOmenKeyPressed);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _omenKey?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnOmenKeyPressed()
    {
        RaiseShell();
        _ = Task.Run(OmenHubSuppressor.Suppress);
    }

    private void RaiseShell()
    {
        void Raise()
        {
            try
            {
                if (MainWindow is MainWindow shell) shell.RaiseToFront();
                else if (MainWindow is not null)
                {
                    MainWindow.Show();
                    MainWindow.Activate();
                }
            }
            catch { }
        }

        if (Dispatcher.CheckAccess()) Raise();
        else Dispatcher.BeginInvoke(Raise);
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



