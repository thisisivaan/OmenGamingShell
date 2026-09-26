using System.Diagnostics;

namespace OmenGamingShell;

public static class OmenHubSuppressor
{
    private static readonly string[] HubProcesses =
    {
        "HP.Omen.OmenCommandCenter",
        "OmenCommandCenterBackground",
        "omenmqtt"
    };

    private const int Passes = 3;
    private const int PassDelayMs = 400;
    private static int _running;

    public static void Suppress()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            for (var pass = 0; pass < Passes; pass++)
            {
                KillHubProcesses();
                if (pass < Passes - 1) Thread.Sleep(PassDelayMs);
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static void KillHubProcesses()
    {
        foreach (var name in HubProcesses)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!IsHubProcess(process)) continue;
                        process.Kill();
                    }
                    catch { }
                }
            }
        }
    }

    private static bool IsHubProcess(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return !string.IsNullOrEmpty(path) &&
                   path.Contains("OMENCommandCenter", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
