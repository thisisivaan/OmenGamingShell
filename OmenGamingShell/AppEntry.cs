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
                if (string.IsNullOrWhiteSpace(Target)) return;
                Process.Start(new ProcessStartInfo($"shell:AppsFolder\\{Target}")
                    { UseShellExecute = true });
                return;
            }
            var target = Environment.ExpandEnvironmentVariables(Target);
            if (string.IsNullOrWhiteSpace(target)) return;
            var isLink = target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            var startInfo = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            };
            if (!isLink && File.Exists(target))
            {
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(directory)) startInfo.WorkingDirectory = directory;
            }
            Process.Start(startInfo);
        }
        catch { }
    }
}