using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;

namespace OmenGamingShell;

public enum BluetoothConnectResult { Success, PairingFailed, ConnectionFailed }

public sealed record WifiNetwork(string Name, int Signal, bool Secured, bool IsConnected, bool IsSaved = false)
{
    public string Summary => $"{Signal}%  •  {(Secured ? "SECURED" : "OPEN")}";
    public string ActionLabel => IsConnected ? "DISCONNECT" : "CONNECT";
    public bool CanForget => IsSaved;
}

public sealed record BluetoothDevice(ulong Address, string Name, bool Connected, bool Paired, string InstanceId = "")
{
    public string Summary => Connected ? "CONNECTED" : Paired ? "PAIRED" : "AVAILABLE";
    public string ActionLabel => Connected ? "DISCONNECT" : "CONNECT";
    public bool CanForget => Paired;
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
                if (!string.IsNullOrWhiteSpace(name)) networks.Add(new(name, signal, secured, name.Equals(connectedSsid, StringComparison.OrdinalIgnoreCase), savedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase)));
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
        if (!string.IsNullOrWhiteSpace(name)) networks.Add(new(name, signal, secured, name.Equals(connectedSsid, StringComparison.OrdinalIgnoreCase), savedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase)));
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

    public static async Task ForgetWifiAsync(WifiNetwork network)
    {
        var escaped = network.Name.Replace("\"", "");
        await RunCheckedAsync("netsh.exe", $"wlan delete profile name=\"{escaped}\"");
    }

    public static async Task ForgetBluetoothDeviceAsync(BluetoothDevice device)
    {
        var tried = new List<string>();
        if (!string.IsNullOrWhiteSpace(device.InstanceId))
        {
            try
            {
                var knownDevice = await DeviceInformation.CreateFromIdAsync(device.InstanceId);
                if (knownDevice?.Pairing is not null)
                {
                    await knownDevice.Pairing.UnpairAsync();
                    return;
                }
            }
            catch { }
        }
        foreach (var address in new[] { device.Address, ReverseBluetoothAddress(device.Address) })
        {
            try
            {
                var btDevice = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(address);
                if (btDevice?.DeviceInformation.Pairing is { } pairing)
                {
                    var result = await pairing.UnpairAsync();
                    if (result.Status == DeviceUnpairingResultStatus.Unpaired) return;
                }
            }
            catch { }
        }
        throw new InvalidOperationException("Could not forget the Bluetooth device.");
    }

    public static async Task<IReadOnlyList<BluetoothDevice>> ScanBluetoothAsync()
    {
        var devices = new List<BluetoothDevice>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dump = Path.Combine(Path.GetTempPath(), "opencode", "bt-scan-dump.txt");
        var sb = new StringBuilder();

        try
        {
            sb.AppendLine($"SCAN classic start");
            var classic = await Task.Run(() => { try { return ScanBluetoothClassic(); } catch (Exception ex) { sb.AppendLine("ERROR classic-scan: " + ex); return Array.Empty<BluetoothDevice>(); } });
            sb.AppendLine($"SCAN classic count={classic.Count}");
            foreach (var device in classic)
            {
                var key = $"addr:{device.Address:X}";
                if (!seenIds.Add(key)) continue;
                devices.Add(device);
            }
        }
        catch (Exception ex) { sb.AppendLine("ERROR classic: " + ex); }

        try
        {
            sb.AppendLine("SCAN winrt start");
            await ScanBluetoothWinRTVariants(devices, seenIds);
            sb.AppendLine($"SCAN winrt total={devices.Count}");
        }
        catch (Exception ex) { sb.AppendLine("ERROR winrt: " + ex); }

        try
        {
            await EnrichConnectedStatusAsync(devices);
            sb.AppendLine($"SCAN enrich done total={devices.Count}");
        }
        catch (Exception ex) { sb.AppendLine("ERROR enrich: " + ex); }

        try { File.AppendAllText(dump, sb.ToString()); } catch { }
        return devices;
    }

    private static async Task EnrichConnectedStatusAsync(List<BluetoothDevice> devices)
    {
        var sb = new StringBuilder();
        var dump = Path.Combine(Path.GetTempPath(), "opencode", "bt-dump.txt");
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            if (device.Address == 0) continue;
            var replaced = false;
            try
            {
                foreach (var address in new[] { device.Address, ReverseBluetoothAddress(device.Address) })
                {
                    string verdict;
                    try
                    {
                        var btDevice = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(address);
                        if (btDevice is null) { verdict = "null"; continue; }
                        var status = btDevice.ConnectionStatus;
                        verdict = $"status={status}";
                        sb.AppendLine($"ENRICH name={device.Name} addr={device.Address:X} cand={address:X} {verdict}");
                        if (status == BluetoothConnectionStatus.Connected)
                        {
                            devices[i] = device with { Connected = true };
                            replaced = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        verdict = "ERR " + ex.GetType().Name + " " + ex.Message;
                        sb.AppendLine($"ENRICH name={device.Name} addr={device.Address:X} cand={address:X} {verdict}");
                    }
                }
            }
            catch { if (replaced) devices[i] = devices[i]; }
        }
        sb.AppendLine($"ENRICH-END count={devices.Count}");
        try { File.AppendAllText(dump, sb.ToString()); } catch { }
    }

    private static IReadOnlyList<BluetoothDevice> ScanBluetoothClassic()
    {
        var devices = new List<BluetoothDevice>();
        var radioParams = new BluetoothFindRadioParams { Size = Marshal.SizeOf<BluetoothFindRadioParams>() };
        var radioSearch = BluetoothFindFirstRadio(ref radioParams, out var radio);
        if (radioSearch == IntPtr.Zero) return devices;
        try
        {
            var searchedRadios = new HashSet<IntPtr>();
            while (true)
            {
                if (radio != IntPtr.Zero && searchedRadios.Add(radio))
                    ScanRadioForDevices(radio, devices);
                if (radio != IntPtr.Zero) CloseHandle(radio);
                if (!BluetoothFindNextRadio(radioSearch, out radio)) break;
            }
        }
        finally
        {
            BluetoothFindRadioClose(radioSearch);
        }
        return devices;
    }

    private static void ScanRadioForDevices(IntPtr radio, List<BluetoothDevice> devices)
    {
        if (radio == IntPtr.Zero) return;
        var searchParams = new BluetoothDeviceSearchParams
        {
            Size = Marshal.SizeOf<BluetoothDeviceSearchParams>(),
            ReturnAuthenticated = true,
            ReturnRemembered = true,
            ReturnUnknown = true,
            ReturnConnected = true,
            IssueInquiry = true,
            TimeoutMultiplier = 5,
            Radio = radio
        };
        var info = new BluetoothDeviceInfo { Size = Marshal.SizeOf<BluetoothDeviceInfo>() };
        var deviceFind = BluetoothFindFirstDevice(ref searchParams, ref info);
        if (deviceFind == IntPtr.Zero) return;
        try
        {
            do
            {
                if (info.Address == 0) continue;
                var rawName = info.Name.Trim();
                var name = string.IsNullOrWhiteSpace(rawName)
                    ? $"Bluetooth device {info.Address:X12}".Substring(0, Math.Min(32, $"Bluetooth device {info.Address:X12}".Length))
                    : rawName;
                devices.Add(new BluetoothDevice(info.Address, name, info.Connected, info.Remembered || info.Authenticated));
            }
            while (BluetoothFindNextDevice(deviceFind, ref info));
        }
        finally { BluetoothFindDeviceClose(deviceFind); }
    }

    private static async Task ScanBluetoothWinRTVariants(List<BluetoothDevice> devices, HashSet<string> seenIds)
    {
        var properties = new List<string>
        {
            "System.Devices.Aep.Connected",
            "System.Devices.Aep.IsConnected",
            "System.Devices.Aep.IsPaired",
            "System.Devices.Aep.DeviceAddress",
            "System.ItemNameDisplay",
        };

        var selectors = new List<string?>
        {
            TrySelector(Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelector),
            TrySelector(() => Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true)),
            TrySelector(Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelector),
            TrySelector(() => Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)),
        };

        foreach (var selector in selectors)
        {
            if (string.IsNullOrEmpty(selector)) continue;
            try
            {
                var items = await DeviceInformation.FindAllAsync(selector, properties);
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.Id)) continue;

                    var name = item.Properties.TryGetValue("System.ItemNameDisplay", out var display) && display is string dn
                        ? dn : item.Name;
                    if (string.IsNullOrWhiteSpace(name))
                        name = item.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addressValue)
                            ? addressValue as string : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var connected = false;
                    if (item.Properties.TryGetValue("System.Devices.Aep.Connected", out var connectedValue) && connectedValue is bool isConnected)
                        connected = isConnected;
                    else if (item.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var isConnectedValue) && isConnectedValue is bool isConnected2)
                        connected = isConnected2;

                    var paired = false;
                    if (item.Properties.TryGetValue("System.Devices.Aep.IsPaired", out var pairedValue) && pairedValue is bool isPaired)
                        paired = isPaired;
                    else paired = item.Pairing?.IsPaired == true;

                    if (!seenIds.Add($"id:{item.Id}")) continue;
                    devices.Add(new BluetoothDevice(TryResolveAddress(item.Id), name, connected, paired, item.Id));
                }
            }
            catch { }
        }
    }

    private static string? TrySelector(Func<string> selector)
    {
        try { return selector(); }
        catch { return null; }
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

    public static async Task<BluetoothConnectResult> ConnectBluetoothAsync(BluetoothDevice device)
    {
        var dump = Path.Combine(Path.GetTempPath(), "opencode", "bt-connect-dump.txt");
        var sb = new StringBuilder();
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var radioOn = true;
            if (radios is not null)
            {
                var bluetoothRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bluetoothRadio is not null && bluetoothRadio.State != RadioState.On)
                {
                    await bluetoothRadio.SetStateAsync(RadioState.On);
                    radioOn = bluetoothRadio.State == RadioState.On;
                }
            }
            sb.AppendLine($"CONNECT name={device.Name} addr={device.Address:X} radioOn={radioOn}");

            var item = await ResolveDeviceInformationAsync(device);
            sb.AppendLine($"CONNECT resolve={(item is null ? "NULL" : "ok")} paired={(item?.Pairing.IsPaired)}");
            if (item is null) return BluetoothConnectResult.PairingFailed;

            if (!item.Pairing.IsPaired)
            {
                var pairResult = await PairSilentlyAsync(item);
                sb.AppendLine($"CONNECT pairing={pairResult.Status}");
                if (pairResult.Status is not DevicePairingResultStatus.Paired
                    and not DevicePairingResultStatus.AlreadyPaired)
                    return BluetoothConnectResult.PairingFailed;
            }

            var before = await IsDeviceActuallyConnectedAsync(device.Address, waitForConnected: false);
            sb.AppendLine($"CONNECT before={before}");
            if (before) return BluetoothConnectResult.Success;
            SetBluetoothAudioServices(device, BluetoothServiceEnable);
            var after = await IsDeviceActuallyConnectedAsync(device.Address, waitForConnected: true);
            sb.AppendLine($"CONNECT after={after}");
            return after ? BluetoothConnectResult.Success : BluetoothConnectResult.ConnectionFailed;
        }
        catch (Exception ex)
        {
            sb.AppendLine("CONNECT ERR " + ex);
            return BluetoothConnectResult.ConnectionFailed;
        }
        finally
        {
            try { File.AppendAllText(dump, sb.ToString()); } catch { }
        }
    }

    private static async Task<DevicePairingResult> PairSilentlyAsync(DeviceInformation device)
    {
        var customPairing = device.Pairing.Custom;
        TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler = (_, args) =>
        {
            try { args.Accept(); } catch { }
        };
        customPairing.PairingRequested += handler;
        try
        {
            return await customPairing.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Default);
        }
        finally
        {
            customPairing.PairingRequested -= handler;
        }
    }

    public static async Task<bool> DisconnectBluetoothAsync(BluetoothDevice device)
    {
        try
        {
            if (!await IsDeviceActuallyConnectedAsync(device.Address, waitForConnected: false)) return true;
            if (!SetBluetoothAudioServices(device, BluetoothServiceDisable)) return false;
            IsDeviceActuallyConnectedAsync(device.Address, waitForConnected: true).GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> IsDeviceActuallyConnectedAsync(ulong address, bool waitForConnected)
    {
        if (address == 0) return false;
        for (var attempt = 0; attempt < (waitForConnected ? 10 : 6); attempt++)
        {
            foreach (var candidate in new[] { address, ReverseBluetoothAddress(address) })
            {
                try
                {
                    var btDevice = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(candidate);
                    if (btDevice is not null)
                    {
                        if (btDevice.ConnectionStatus == BluetoothConnectionStatus.Connected) return true;
                        break;
                    }
                }
                catch { }
            }
            if (!waitForConnected) break;
            await Task.Delay(500);
        }
        return false;
    }

    public static async Task<bool> PairBluetoothAsync(BluetoothDevice device)
    {
        try
        {
            var item = await ResolveDeviceInformationAsync(device);
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
            var item = await ResolveDeviceInformationAsync(device);
            if (item is null) return false;
            if (!item.Pairing.IsPaired) return true;
            var result = await item.Pairing.UnpairAsync();
            return result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired;
        }
        catch { return false; }
    }

    private static async Task<DeviceInformation?> ResolveDeviceInformationAsync(BluetoothDevice device)
    {
        if (device.Address != 0)
        {
            foreach (var address in new[] { device.Address, ReverseBluetoothAddress(device.Address) })
            {
                try
                {
                    var btDevice = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(address);
                    if (btDevice is not null) return btDevice.DeviceInformation;
                }
                catch { }
            }
        }
        try
        {
            if (string.IsNullOrWhiteSpace(device.InstanceId)) return null;
            return await DeviceInformation.CreateFromIdAsync(device.InstanceId);
        }
        catch { return null; }
    }

    private static ulong ReverseBluetoothAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        Array.Reverse(bytes, 0, 6);
        return BitConverter.ToUInt64(bytes, 0);
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
        var dump = Path.Combine(Path.GetTempPath(), "opencode", "bt-connect-dump.txt");
        var sb = new StringBuilder();
        var radio = OpenRadio(out var search);
        try
        {
            if (radio == IntPtr.Zero)
            {
                sb.AppendLine($"SETSVC name={device.Name} addr={device.Address:X} flag={flag} radio=ZERO search={search}");
                return false;
            }
            var deviceInfo = new BluetoothDeviceInfo
            {
                Size = Marshal.SizeOf<BluetoothDeviceInfo>(),
                Address = device.Address,
                Name = device.Name
            };
            var serviceGuids = new List<Guid>();
            uint serviceCount = 0;
            var enumResult = BluetoothEnumerateInstalledServices(radio, ref deviceInfo, ref serviceCount, null);
            sb.AppendLine($"SETSVC enum pass1 result={enumResult} count={serviceCount}");
            if (enumResult == 0 && serviceCount > 0)
            {
                var guids = new Guid[serviceCount];
                var filled = serviceCount;
                var enumResult2 = BluetoothEnumerateInstalledServices(radio, ref deviceInfo, ref filled, guids);
                sb.AppendLine($"SETSVC enum pass2 result={enumResult2} filled={filled}");
                for (uint i = 0; i < Math.Min(filled, (uint)guids.Length); i++)
                    serviceGuids.Add(guids[i]);
            }
            if (serviceGuids.Count == 0)
                serviceGuids.AddRange(new[] { A2dpSinkServiceGuid, A2dpSourceServiceGuid, HfpAudioGatewayGuid, HspAudioGatewayGuid });
            var activated = false;
            foreach (var svcGuid in serviceGuids)
            {
                var guid = svcGuid;
                var result = BluetoothSetServiceState(radio, ref deviceInfo, ref guid, flag);
                sb.AppendLine($"SETSVC {guid} flag={flag} result={result} winerr={Marshal.GetLastWin32Error()}");
                if (result == 0) activated = true;
            }
            sb.AppendLine($"SETSVC activated={activated}");
            return activated;
        }
        catch (Exception ex)
        {
            sb.AppendLine("SETSVC ERR " + ex);
            return false;
        }
        finally
        {
            try { File.AppendAllText(dump, sb.ToString()); } catch { }
            if (radio != IntPtr.Zero) CloseHandle(radio); if (search != IntPtr.Zero) BluetoothFindRadioClose(search);
        }
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
    [DllImport("BluetoothApis.dll")] private static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);
    [DllImport("BluetoothApis.dll")] private static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode)] private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams search, ref BluetoothDeviceInfo info);
    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode)] private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDeviceInfo info);
    [DllImport("BluetoothApis.dll")] private static extern bool BluetoothFindDeviceClose(IntPtr find);
    [DllImport("BluetoothApis.dll")] private static extern uint BluetoothEnumerateInstalledServices(IntPtr radio, ref BluetoothDeviceInfo deviceInfo, ref uint serviceCount, Guid[]? serviceGuids);
    [DllImport("BluetoothApis.dll")] private static extern uint BluetoothSetServiceState(IntPtr radio, ref BluetoothDeviceInfo deviceInfo, ref Guid serviceGuid, uint flags);
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
