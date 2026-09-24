using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace OmenGamingShell;

public static class WindowsUninstallRegistry
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly (RegistryHive Hive, RegistryView View)[] Roots =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    };

    public static void RemoveForGame(GameEntry game)
    {
        var folder = game.UninstallFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;
        var denied = new List<string>();
        foreach (var (hive, view) in Roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(UninstallRoot, writable: true);
                if (uninstallKey is null) continue;
                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    bool matches;
                    try
                    {
                        using var sub = uninstallKey.OpenSubKey(subKeyName);
                        matches = sub is not null && IsMatch(sub, game.Name, folder);
                    }
                    catch (Exception) { matches = false; }
                    if (!matches) continue;

                    try
                    {
                        uninstallKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        var hivePath = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                        denied.Add($"Registry::{hivePath}\\{UninstallRoot}\\{subKeyName}");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        if (denied.Count > 0) ElevateRemoval(denied);
    }

    private static bool IsMatch(RegistryKey sub, string gameName, string folder)
    {
        if (ReferencesFolder(GetString(sub, "InstallLocation"), folder) ||
            ReferencesFolder(GetString(sub, "InstallDir"), folder) ||
            ReferencesFolder(GetString(sub, "Inno Setup: App Path"), folder) ||
            ReferencesFolder(GetString(sub, "DisplayIcon"), folder) ||
            ReferencesFolder(GetString(sub, "UninstallString"), folder) ||
            ReferencesFolder(GetString(sub, "QuietUninstallString"), folder) ||
            ReferencesFolder(GetString(sub, "ModifyPath"), folder))
            return true;

        var displayName = GetString(sub, "DisplayName");
        return !string.IsNullOrWhiteSpace(displayName) &&
               displayName.Equals(gameName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(RegistryKey key, string valueName)
    {
        try { return key.GetValue(valueName) as string; }
        catch (Exception) { return null; }
    }

    private static bool ReferencesFolder(string? value, string folder)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = NormalizePath(value);
        var target = NormalizePath(folder);
        if (normalized is null || target is null) return false;
        return normalized.Equals(target, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"')
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 0)
            {
                trimmed = trimmed[(end + 1)..].TrimStart();
                if (trimmed.StartsWith(",", StringComparison.Ordinal)) return null;
            }
        }
        try { return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception) { return null; }
    }

    private static void ElevateRemoval(List<string> paths)
    {
        try
        {
            var joined = string.Join(" ", paths.Select(p => $"\"{p}\""));
            var args = $"-NoProfile -ExecutionPolicy Bypass -Command \"foreach ($p in @({joined})) {{ if (Test-Path -LiteralPath $p) {{ Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop }} }}\"";
            Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception exception)
        {
            ErrorLogStore.Log("Removing Windows uninstall entries needs elevation", "Uninstall", exception.ToString());
        }
    }
}