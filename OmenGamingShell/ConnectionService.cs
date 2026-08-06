using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace OmenGamingShell;

public sealed record WifiNetwork(string Name, int Signal, bool Secured, bool IsConnected)
{
    public string Summary => $"{Signal}%  •  {(Secured ? "SECURED" : "OPEN")}";
    public string ActionLabel => IsConnected ? "DISCONNECT" : "CONNECT";
}

public sealed record BluetoothDevice(ulong Address, string Name, bool Connected, bool Paired)
{
    public string Summary => Connected ? "CONNECTED" : Paired ? "PAIRED" : "AVAILABLE";
    public string ActionLabel => Connected ? "DISCONNECT" : "CONNECT";
}

public static class ConnectionService
{
    public static async Task<IReadOnlyList<WifiNetwork>> ScanWifiAsync()
    {
        var profilesOutput = await RunAsync("netsh.exe", "wlan show profiles");
        var savedProfiles = Regex.Matches(profilesOutput,
                @"^\s*(?:All User Profile|Current User Profile)\s*:\s*(.+)$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        TriggerWifiScan(savedProfiles);
        await Task.Delay(2200);
        var interfaceOutput = await RunAsync("netsh.exe", "wlan show interfaces");
        var connectedMatch = Regex.Match(interfaceOutput, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var connectedSsid = connectedMatch.Success ? connectedMatch.Groups[1].Value.Trim() : string.Empty;
        var output = await RunAsync("netsh.exe", "wlan show networks mode=bssid");
        var networks = new List<WifiNetwork>();
        string? name = null; var signal = 0; var secured = true;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var ssid = Regex.Match(line, @"^SSID\s+\d+\s*:\s*(.*)$", RegexOptions.IgnoreCase);
            if (ssid.Success)
            {
                if (!string.IsNullOrWhiteSpace(name)) networks.Add(new(name, signal, secured, name.Equals(connectedSsid, StringComparison.OrdinalIgnoreCase)));
                name = ssid.Groups[1].Value.Trim(); signal = 0; secured = true;
            }
            else if (line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase))
                secured = !line.Contains("Open", StringComparison.OrdinalIgnoreCase);
            else
            {
                var match = Regex.Match(line, @"^Signal\s*:\s*(\d+)%", RegexOptions.IgnoreCase);
                if (match.Success) signal = Math.Max(signal, int.Parse(match.Groups[1].Value));
            }
        }
        if (!string.IsNullOrWhiteSpace(name)) networks.Add(new(name, signal, secured, name.Equals(connectedSsid, StringComparison.OrdinalIgnoreCase)));
        return networks.GroupBy(network => network.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(network => network.Signal).First())
            .OrderByDescending(network => network.IsConnected).ThenByDescending(network => network.Signal).ToList();
    }

    private static void TriggerWifiScan(IReadOnlyList<string> savedProfiles)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0) return;
        try
        {
            if (WlanEnumInterfaces(client, IntPtr.Zero, out var list) != 0 || list == IntPtr.Zero) return;
            try
            {
                var count = Marshal.ReadInt32(list);
                var offset = 8;
                var size = Marshal.SizeOf<WlanInterfaceInfo>();
                for (var index = 0; index < count; index++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(list, offset + index * size));
                    WlanScan(client, ref info.Id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    foreach (var profile in savedProfiles)
                    {
                        var bytes = Encoding.UTF8.GetBytes(profile);
                        if (bytes.Length is 0 or > 32) continue;
                        var ssid = new Dot11Ssid { Length = (uint)bytes.Length, Value = new byte[32] };
                        Array.Copy(bytes, ssid.Value, bytes.Length);
                        var ssidPointer = Marshal.AllocHGlobal(Marshal.SizeOf<Dot11Ssid>());
                        try
                        {
                            Marshal.StructureToPtr(ssid, ssidPointer, false);
                            WlanScan(client, ref info.Id, ssidPointer, IntPtr.Zero, IntPtr.Zero);
                        }
                        finally { Marshal.FreeHGlobal(ssidPointer); }
                    }
                }
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(client, IntPtr.Zero); }
    }

    public static async Task ConnectWifiAsync(WifiNetwork network, string? password)
    {
        if (network.Secured && !string.IsNullOrWhiteSpace(password))
        {
            var escapedName = SecurityElement.Escape(network.Name) ?? network.Name;
            var escapedPassword = SecurityElement.Escape(password) ?? password;
            var profile = $"<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>{escapedName}</name><SSIDConfig><SSID><name>{escapedName}</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{escapedPassword}</keyMaterial></sharedKey></security></MSM></WLANProfile>";
            var profilePath = Path.Combine(Path.GetTempPath(), $"omen-wifi-{Guid.NewGuid():N}.xml");
            try
            {
                await File.WriteAllTextAsync(profilePath, profile);
                await RunCheckedAsync("netsh.exe", $"wlan add profile filename=\"{profilePath}\" user=current");
            }
            finally { try { File.Delete(profilePath); } catch { } }
        }
        await RunCheckedAsync("netsh.exe", $"wlan connect name=\"{network.Name.Replace("\"", "") }\"");
    }

    public static Task DisconnectWifiAsync() => RunCheckedAsync("netsh.exe", "wlan disconnect");

    public static IReadOnlyList<BluetoothDevice> ScanBluetooth()
    {
        var devices = new List<BluetoothDevice>();
        var radio = OpenRadio(out var radioSearch);
        if (radio == IntPtr.Zero) return devices;
        try
        {
            var search = new BluetoothDeviceSearchParams
            {
                Size = Marshal.SizeOf<BluetoothDeviceSearchParams>(), ReturnAuthenticated = true,
                ReturnRemembered = true, ReturnUnknown = true, ReturnConnected = true,
                IssueInquiry = true, TimeoutMultiplier = 4, Radio = radio
            };
            var info = new BluetoothDeviceInfo { Size = Marshal.SizeOf<BluetoothDeviceInfo>() };
            var find = BluetoothFindFirstDevice(ref search, ref info);
            if (find == IntPtr.Zero) return devices;
            try
            {
                do
                {
                    if (!string.IsNullOrWhiteSpace(info.Name))
                        devices.Add(new(info.Address, info.Name, info.Connected, info.Authenticated || info.Remembered));
                    info.Size = Marshal.SizeOf<BluetoothDeviceInfo>();
                } while (BluetoothFindNextDevice(find, ref info));
            }
            finally { BluetoothFindDeviceClose(find); }
        }
        finally { CloseHandle(radio); BluetoothFindRadioClose(radioSearch); }
        return devices.GroupBy(device => device.Address).Select(group => group.First()).ToList();
    }

    public static bool PairBluetooth(IntPtr parent, BluetoothDevice device)
    {
        var radio = OpenRadio(out var search);
        if (radio == IntPtr.Zero) return false;
        try
        {
            var info = new BluetoothDeviceInfo { Size = Marshal.SizeOf<BluetoothDeviceInfo>(), Address = device.Address, Name = device.Name };
            return BluetoothAuthenticateDeviceEx(parent, radio, ref info, IntPtr.Zero, 0) == 0;
        }
        finally { CloseHandle(radio); BluetoothFindRadioClose(search); }
    }

    public static bool RemoveBluetooth(BluetoothDevice device)
    {
        var address = device.Address;
        return BluetoothRemoveDevice(ref address) == 0;
    }

    private static IntPtr OpenRadio(out IntPtr search)
    {
        var parameters = new BluetoothFindRadioParams { Size = Marshal.SizeOf<BluetoothFindRadioParams>() };
        search = BluetoothFindFirstRadio(ref parameters, out var radio);
        return search == IntPtr.Zero ? IntPtr.Zero : radio;
    }

    private static async Task<string> RunAsync(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
        if (process is null) throw new InvalidOperationException("Could not start network service.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return output;
    }
    private static async Task RunCheckedAsync(string file, string arguments) => _ = await RunAsync(file, arguments);

    [StructLayout(LayoutKind.Sequential)] private struct BluetoothFindRadioParams { public int Size; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public int Size; public ulong Address; public uint ClassOfDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool Connected;
        [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
        [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
    {
        public int Size;
        [MarshalAs(UnmanagedType.Bool)] public bool ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        public byte TimeoutMultiplier; public IntPtr Radio;
    }
    [DllImport("BluetoothApis.dll")] private static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams parameters, out IntPtr radio);
    [DllImport("BluetoothApis.dll")] private static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode)] private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams search, ref BluetoothDeviceInfo info);
    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode)] private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDeviceInfo info);
    [DllImport("BluetoothApis.dll")] private static extern bool BluetoothFindDeviceClose(IntPtr find);
    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode)] private static extern uint BluetoothAuthenticateDeviceEx(IntPtr parent, IntPtr radio, ref BluetoothDeviceInfo info, IntPtr callback, int requirement);
    [DllImport("BluetoothApis.dll")] private static extern uint BluetoothRemoveDevice(ref ulong address);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo { public Guid Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; public int State; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Value;
    }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceId, IntPtr dot11Ssid, IntPtr ieData, IntPtr reserved);
}
