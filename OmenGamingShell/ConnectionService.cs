using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace OmenGamingShell;

public sealed record WifiNetwork(string Name, int Signal, bool Secured, bool IsConnected)
{
    public string Summary => $"{Signal}%  •  {(Secured ? "SECURED" : "OPEN")}";
    public string ActionLabel => IsConnected ? "DISCONNECT" : "CONNECT";
}

public sealed record BluetoothDevice(ulong Address, string Name, bool Connected, bool Paired, string InstanceId = "")
{
    public string Summary => Connected ? "CONNECTED" : Paired ? "PAIRED" : "AVAILABLE";
    public string ActionLabel => Connected ? "DISCONNECT" : "CONNECT";
}

public static class ConnectionService
{
    private static readonly Guid A2dpSinkServiceGuid = new("0000110b-0000-1000-8000-00805f9b34fb");
    private static readonly Guid A2dpSourceServiceGuid = new("0000110a-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HfpAudioGatewayGuid = new("0000111e-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HspAudioGatewayGuid = new("00001108-0000-1000-8000-00805f9b34fb");
    private const uint BluetoothServiceEnable = 1;
    private const uint BluetoothServiceDisable = 0;

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

    public static async Task<IReadOnlyList<BluetoothDevice>> ScanBluetoothAsync()
    {
        var devices = new List<BluetoothDevice>();
        try
        {
            var radios = await Radio.GetRadiosAsync();
            if (radios is null || !radios.Any(r => r.Kind == RadioKind.Bluetooth)) return devices;
            var items = await DeviceInformation.FindAllAsync(Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelector(),
                new List<string> { "System.Devices.Aep.Connected", "System.ItemNameDisplay" });
            foreach (var item in items)
            {
                var name = item.Properties.TryGetValue("System.ItemNameDisplay", out var display)
                    ? display as string : item.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var connected = false;
                if (item.Properties.TryGetValue("System.Devices.Aep.Connected", out var connectedValue) && connectedValue is bool isConnected)
                    connected = isConnected;
                var paired = item.Pairing?.IsPaired == true;
                var address = TryResolveAddress(item.Id);
                devices.Add(new BluetoothDevice(address, name, connected, paired, item.Id));
            }
        }
        catch { }
        return devices;
    }

    private static ulong TryResolveAddress(string deviceId)
    {
        try
        {
            var marker = deviceId.IndexOf("Bluetooth#Bluetooth", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                marker = deviceId.IndexOf("BluetoothLE#BluetoothLE", StringComparison.OrdinalIgnoreCase);
                if (marker < 0) return 0;
            }
            var skip = deviceId[marker..].IndexOf('#') + 1;
            var rest = deviceId[(marker + skip)..];
            var end = rest.IndexOf('-');
            var mac = (end > 0 ? rest[..end] : rest).Replace(":", "").Replace("-", "");
            if (!ulong.TryParse(mac, System.Globalization.NumberStyles.HexNumber, null, out var value) || mac.Length != 12)
                return 0;
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes, 0, 6);
            return BitConverter.ToUInt64(bytes, 0);
        }
        catch { return 0; }
    }

    public static async Task<bool> ConnectBluetoothAsync(BluetoothDevice device)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            if (radios is not null)
            {
                var bluetoothRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bluetoothRadio is not null && bluetoothRadio.State != RadioState.On)
                    await bluetoothRadio.SetStateAsync(RadioState.On);
            }

            var item = await DeviceInformation.CreateFromIdAsync(device.InstanceId);
            if (item is null) return false;

            if (!item.Pairing.IsPaired)
            {
                var result = await item.Pairing.PairAsync(DevicePairingProtectionLevel.Default);
                if (result.Status is not DevicePairingResultStatus.Paired
                    and not DevicePairingResultStatus.AlreadyPaired) return false;
            }

            SetBluetoothAudioServices(device, BluetoothServiceEnable);
            return true;
        }
        catch { return false; }
    }

    public static Task<bool> DisconnectBluetoothAsync(BluetoothDevice device)
    {
        try
        {
            return Task.FromResult(SetBluetoothAudioServices(device, BluetoothServiceDisable));
        }
        catch { return Task.FromResult(false); }
    }

    public static async Task<bool> PairBluetoothAsync(BluetoothDevice device)
    {
        try
        {
            var item = await DeviceInformation.CreateFromIdAsync(device.InstanceId);
            if (item is null) return false;
            if (item.Pairing.IsPaired) return true;
            var result = await item.Pairing.PairAsync(DevicePairingProtectionLevel.Default);
            return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
        }
        catch { return false; }
    }

    public static async Task<bool> RemoveBluetoothAsync(BluetoothDevice device)
    {
        try
        {
            var item = await DeviceInformation.CreateFromIdAsync(device.InstanceId);
            if (item is null) return false;
            if (!item.Pairing.IsPaired) return true;
            var result = await item.Pairing.UnpairAsync();
            return result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired;
        }
        catch { return false; }
    }

    public static async Task<bool> IsBluetoothRadioAvailableAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            return radios is not null && radios.Any(r => r.Kind == RadioKind.Bluetooth);
        }
        catch { return false; }
    }

    private static bool SetBluetoothAudioServices(BluetoothDevice device, uint flag)
    {
        var address = device.Address;
        var radio = OpenRadio(out var search);
        try
        {
            if (radio == IntPtr.Zero) return false;
            var serviceCount = 0u;
            BluetoothEnumerateInstalledServices(radio, ref address, ref serviceCount, null);
            if (serviceCount == 0) return false;
            var serviceGuids = new Guid[serviceCount];
            BluetoothEnumerateInstalledServices(radio, ref address, ref serviceCount, serviceGuids);
            var audioGuids = new[] { A2dpSinkServiceGuid, A2dpSourceServiceGuid, HfpAudioGatewayGuid, HspAudioGatewayGuid };
            var activated = false;
            foreach (var svcGuid in serviceGuids)
            {
                var guid = svcGuid;
                var isAudio = Array.Exists(audioGuids, g => g == guid);
                var desiredFlag = isAudio ? flag : BluetoothServiceDisable;
                if (BluetoothSetServiceState(radio, ref address, ref guid, desiredFlag) == 0)
                    activated = true;
            }
            return activated;
        }
        catch { return false; }
        finally { if (radio != IntPtr.Zero) CloseHandle(radio); if (search != IntPtr.Zero) BluetoothFindRadioClose(search); }
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
    [DllImport("BluetoothApis.dll")] private static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams parameters, out IntPtr radio);
    [DllImport("BluetoothApis.dll")] private static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("BluetoothApis.dll")] private static extern uint BluetoothEnumerateInstalledServices(IntPtr radio, ref ulong address, ref uint serviceCount, Guid[]? serviceGuids);
    [DllImport("BluetoothApis.dll")] private static extern uint BluetoothSetServiceState(IntPtr radio, ref ulong address, ref Guid serviceGuid, uint flags);
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
