using System.Diagnostics;
using System.Text.Json;

namespace OmenGamingShell;

public sealed record ConnectedDeviceEntry(string Name, string Connection, string DeviceClass, string Status, string Battery)
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
            if (value.Contains("headphone") || value.Contains("headset") || value.Contains("earphone") || value.Contains("earbud") || value.Contains("audio") || value.Contains("speaker")) return "\uE7F5";
            if (value.Contains("watch") || value.Contains("band")) return "\uE935";
            return "\uE88E";
        }
    }

    public string StatusDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Battery)) return $"BAT {Battery}";
            return Status;
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
                Arguments = "-NoProfile -NonInteractive -Command \"$bt = Get-PnpDevice -PresentOnly | Where-Object { $_.Status -eq 'OK' -and $_.InstanceId -like 'BTH*' } | Select-Object FriendlyName,Class,InstanceId,Status; $usb = Get-PnpDevice -PresentOnly | Where-Object { $_.Status -eq 'OK' -and $_.InstanceId -like 'USB*' } | Select-Object FriendlyName,Class,InstanceId,Status; $all = @(); if ($bt) { $all += $bt }; if ($usb) { $all += $usb }; $all | ConvertTo-Json -Compress\""
            });
            if (process is null) return devices;
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return devices;
            using var document = JsonDocument.Parse(json);
            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : new[] { document.RootElement };
            foreach (var entry in entries)
            {
                var name = entry.TryGetProperty("FriendlyName", out var nameValue) ? nameValue.GetString() : null;
                var deviceClass = entry.TryGetProperty("Class", out var classValue) ? classValue.GetString() : null;
                var status = entry.TryGetProperty("Status", out var statusValue) ? statusValue.GetString() : "OK";
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
                // Filter out built-in cameras
                var cameraTerms = new[] { "camera", "webcam", "integrated camera", "truevision", "ir camera", "rgb camera", "depth camera", "visual computing" };
                if (cameraTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
                var cameraClasses = new[] { "Camera", "Image" };
                if (cameraClasses.Any(c => string.Equals(deviceClass, c, StringComparison.OrdinalIgnoreCase))) continue;
                var connection = string.Equals(deviceClass, "Bluetooth", StringComparison.OrdinalIgnoreCase)
                    ? "BLUETOOTH" : "USB / USB-C";
                devices.Add(new(name.Trim(), connection, deviceClass ?? string.Empty, status?.ToUpperInvariant() ?? "OK", string.Empty));
            }
        }
        catch { }

        // Try to get battery levels
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = "-NoProfile -NonInteractive -Command \"Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'BTH*' -and $_.Status -eq 'OK' } | Get-PnpDeviceProperty -KeyName 'DEVPKEY_Bluetooth_DeviceInstancePath','DEVPKEY_Device_BatteryLevel' -ErrorAction SilentlyContinue | Select-Object -Property * | ConvertTo-Json -Compress -Depth 3\""
            });
            if (process is not null)
            {
                var batteryJson = process.StandardOutput.ReadToEnd();
                process.WaitForExit(6000);
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(batteryJson))
                {
                    // Battery info from PnP is often unavailable; try wmic as fallback
                    try
                    {
                        using var wmic = Process.Start(new ProcessStartInfo("wmic")
                        {
                            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                            CreateNoWindow = true,
                            Arguments = "/namespace:\\\\root\\wmi PATH BatteryStatus get DeviceName,RemainingCapacity,MaxCapacity /format:csv"
                        });
                        if (wmic is not null)
                        {
                            var wmicOut = wmic.StandardOutput.ReadToEnd();
                            wmic.WaitForExit(5000);
                            if (wmic.ExitCode == 0 && !string.IsNullOrWhiteSpace(wmicOut))
                            {
                                var lines = wmicOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                                foreach (var line in lines.Skip(1))
                                {
                                    var parts = line.Split(',', StringSplitOptions.TrimEntries);
                                    if (parts.Length >= 4 && int.TryParse(parts[2], out var remaining) && int.TryParse(parts[3], out var max) && max > 0)
                                    {
                                        var pct = Math.Round(remaining * 100.0 / max);
                                        var deviceName = parts[1];
                                        var match = devices.FirstOrDefault(d => d.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase)
                                            || deviceName.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
                                        if (match is not null) devices = devices.Where(d => !ReferenceEquals(d, match))
                                            .Append(match with { Battery = $"{pct}%" }).ToList();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // Smart dedup: if one name is a substring of another, keep the longer one
        var deduped = new List<ConnectedDeviceEntry>();
        var sorted = devices.OrderBy(d => d.Name.Length).ToList();
        foreach (var device in sorted)
        {
            var isDuplicate = deduped.Any(existing =>
                device.Name.Contains(existing.Name, StringComparison.OrdinalIgnoreCase) ||
                existing.Name.Contains(device.Name, StringComparison.OrdinalIgnoreCase));
            if (!isDuplicate) deduped.Add(device);
        }

        return deduped.OrderBy(device => device.Connection).ThenBy(device => device.Name).ToList();
    }
}
