using System.Diagnostics;
using System.IO;

namespace OmenGamingShell;

public sealed class AppEntry
{
    public string Name { get; set; } = "Untitled";
    public string Target { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public bool IsUwp { get; set; }

    public string Cover => Icon;

    public void Launch()
    {
        try
        {
            if (IsUwp)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{Target}")
                    { UseShellExecute = false });
                return;
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = Target,
                UseShellExecute = Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            };
            var directory = !IsUwp && Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(Target)
                : null;
            if (!string.IsNullOrWhiteSpace(directory)) startInfo.WorkingDirectory = directory;
            Process.Start(startInfo);
        }
        catch { }
    }
}