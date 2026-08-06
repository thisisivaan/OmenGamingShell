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
    }

    private void HandleCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
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
