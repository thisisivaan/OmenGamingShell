using System.Diagnostics;
using System.Text.Json;

namespace OmenGamingShell;

public sealed record ConnectedDeviceEntry(string Name, string Connection, string DeviceClass)
{
    public string Glyph
    {
        get
        {
            var value = $"{Name} {DeviceClass}".ToLowerInvariant();
            if (value.Contains("phone") || value.Contains("mobile") || value.Contains("android") || value.Contains("iphone")) return "\uE8EA";
            if (value.Contains("mouse") || value.Contains("pointing")) return "\uE962";
            if (value.Contains("controller") || value.Contains("gamepad") || value.Contains("xbox") || value.Contains("dualsense") || value.Contains("dualshock")) return "\uE7FC";
            if (value.Contains("keyboard") || value.Contains("keypad")) return "\uE765";
            return "\uE88E";
        }
    }
}

public static class ConnectedDeviceScanner
{
    public static IReadOnlyList<ConnectedDeviceEntry> Scan()
    {
        var devices = new List<ConnectedDeviceEntry>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = "-NoProfile -NonInteractive -Command \"Get-PnpDevice -PresentOnly | Where-Object { $_.Status -eq 'OK' -and ($_.InstanceId -like 'BTH*' -or $_.InstanceId -like 'USB*') } | Select-Object FriendlyName,Class,InstanceId | ConvertTo-Json -Compress\""
            });
            if (process is null) return devices;
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return devices;
            using var document = JsonDocument.Parse(json);
            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : new[] { document.RootElement };
            foreach (var entry in entries)
            {
                var name = entry.TryGetProperty("FriendlyName", out var nameValue) ? nameValue.GetString() : null;
                var deviceClass = entry.TryGetProperty("Class", out var classValue) ? classValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var technicalNames = new[]
                {
                    "host controller", "root hub", "composite device", "enumerator",
                    "bluetooth device (rfcomm", "generic usb hub",
                    "bluetooth le generic attribute service", "generic attribute profile",
                    "generic access profile", "device information service",
                    "service discovery service", "personal area network", "nap service",
                    "a2dp", "avrcp", "hands-free", "phonebook",
                    "wireless iap", "app mode", "aap client", "map mas",
                    "adapter", "gatt", "battery service"
                };
                if (technicalNames.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
                var connection = string.Equals(deviceClass, "Bluetooth", StringComparison.OrdinalIgnoreCase)
                    ? "BLUETOOTH" : "USB / USB-C";
                devices.Add(new(name.Trim(), connection, deviceClass ?? string.Empty));
            }
        }
        catch { }
        return devices.GroupBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).OrderBy(device => device.Connection).ThenBy(device => device.Name).ToList();
    }
}
