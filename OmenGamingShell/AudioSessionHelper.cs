using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OmenGamingShell;

// Mutes the audio of a specific process family (an Inno Setup install that
// plays music via its own BASS/ISDone window) without touching the rest of the
// system's audio. Uses the Windows Core Audio MMDevice API: find every active
// render endpoint, activate its IAudioSessionManager2, enumerate the audio
// sessions and flip SetMute on the sessions owned by the given process (and any
// children it spawned). The ComImport declarations below mirror NAudio's proven
// interop bit-for-bit (PreserveSig HRESULT returns, base-interface inheritance,
// UnmanagedType.IUnknown activation), because getting any vtable slot wrong
// silently misbehaves.
public static partial class AudioSessionHelper
{
    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    // Mutes every audio session whose process is `rootPid` or any of its
    // descendants (setup.exe spawns setup.tmp, xtool.exe, etc.). Returns the
    // number of sessions actually muted (0 when nothing was found yet).
    public static int MuteProcessFamily(int rootPid)
    {
        var family = GetProcessFamily(rootPid);
        if (family.Count == 0) return 0;
        return MuteSessionsByPids(family);
    }

    // Debug: print every render session pid across all active devices.
    public static void DebugListSessions()
    {
        IMMDeviceEnumerator enumerator;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }
        catch (Exception ex) { Console.WriteLine($"DEV0 {ex.Message}"); return; }
        int hr = enumerator.EnumAudioEndpoints(0, DEVICE_STATE_ACTIVE, out var collection);
        Console.WriteLine($"enum hr=0x{hr:x8} coll={(collection is null ? "null" : "ok")}");
        if (hr != 0 || collection is null) return;
        collection.GetCount(out uint deviceCount);
        Console.WriteLine($"devices={deviceCount}");
        for (uint i = 0; i < deviceCount; i++)
        {
            collection.Item(i, out var device);
            Console.WriteLine($"  item[{i}] device={(device is null ? "null" : "ok")}");
            if (device is null) continue;
            var manager = ActivateManager(device);
            Console.WriteLine($"  item[{i}] manager={(manager is null ? "null" : "ok")}");
            if (manager is null) continue;
            int hr2 = manager.GetSessionEnumerator(out var sessionEnum);
            Console.WriteLine($"  item[{i}] sessEnum hr=0x{hr2:x8} val={(sessionEnum is null ? "null" : "ok")}");
            if (sessionEnum is null) continue;
            sessionEnum.GetCount(out int count);
            Console.WriteLine($"  item[{i}] sessCount={count}");
            for (int k = 0; k < count; k++)
            {
                sessionEnum.GetSession(k, out var control);
                uint pid = 0;
                if (control is IAudioSessionControl2 c2) { try { c2.GetProcessId(out pid); } catch { } }
                Console.WriteLine($"    sess[{k}] pid={pid}");
            }
        }
    }

    // ------------------------------------------------------------------
    // Process tree (Toolhelp32)
    // ------------------------------------------------------------------

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private static List<(uint Pid, uint ParentPid)> SnapshotAllProcesses()
    {
        var list = new List<(uint, uint)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return list;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    list.Add((entry.th32ProcessID, entry.th32ParentProcessID));
                } while (Process32Next(snapshot, ref entry));
            }
        }
        catch
        {
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return list;
    }

    private static HashSet<int> GetProcessFamily(int rootPid)
    {
        var family = new HashSet<int>();
        var all = SnapshotAllProcesses();
        if (all.Count == 0)
        {
            family.Add(rootPid);
            return family;
        }

        family.Add(rootPid);
        bool added = true;
        for (int pass = 0; pass < 20 && added; pass++)
        {
            added = false;
            foreach (var (pid, parent) in all)
            {
                if (family.Contains((int)parent) && !family.Contains((int)pid))
                {
                    family.Add((int)pid);
                    added = true;
                }
            }
        }
        return family;
    }

    // ------------------------------------------------------------------
    // Core Audio MMDevice API (NAudio-compatible interop)
    // ------------------------------------------------------------------

    private const int CLSCTX_ALL = 0x17;
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;

    private static readonly Guid ManagerIid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint masks, out IMMDeviceCollection collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid id, int clsCtx, IntPtr activationParams,
            out IntPtr interfacePointer);
    }

    // Base IAudioSessionManager (first two vtable slots).
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("BFA971F1-4D5E-40BB-935E-967039BFBEE4")]
    private interface IAudioSessionManager
    {
        [PreserveSig] int GetAudioSessionControl(
            [In] [MarshalAs(UnmanagedType.LPStruct)] Guid sessionId,
            [In] uint streamFlags,
            [Out] [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl sessionControl);

        [PreserveSig] int GetSimpleAudioVolume(
            [In] [MarshalAs(UnmanagedType.LPStruct)] Guid sessionId,
            [In] uint streamFlags,
            [Out] [MarshalAs(UnmanagedType.Interface)] out ISimpleAudioVolume audioVolume);
    }

    // IAudioSessionManager2 extends the base: GetSessionEnumerator is slot 2.
    // ComImport inheritance requires redeclaring every base method with `new`
    // or the CLR lays out the vtable wrong (E_INVALIDARG).
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    private interface IAudioSessionManager2 : IAudioSessionManager
    {
        [PreserveSig] new int GetAudioSessionControl(
            [In] [MarshalAs(UnmanagedType.LPStruct)] Guid sessionId,
            [In] uint streamFlags,
            [Out] [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl sessionControl);

        [PreserveSig] new int GetSimpleAudioVolume(
            [In] [MarshalAs(UnmanagedType.LPStruct)] Guid sessionId,
            [In] uint streamFlags,
            [Out] [MarshalAs(UnmanagedType.Interface)] out ISimpleAudioVolume audioVolume);

        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notify);
        [PreserveSig] int RegisterDuckNotification(string sessionId, IntPtr notify);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notify);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    // IAudioSessionControl: 9 methods spanning slots 0-8.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, [In] ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, [In] ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam([In] ref Guid group, [In] ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
    }

    // IAudioSessionControl2 extends the base: GetProcessId is slot 11.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig] new int GetState(out int state);
        [PreserveSig] new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, [In] ref Guid ctx);
        [PreserveSig] new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, [In] ref Guid ctx);
        [PreserveSig] new int GetGroupingParam(out Guid group);
        [PreserveSig] new int SetGroupingParam([In] ref Guid group, [In] ref Guid ctx);
        [PreserveSig] new int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] new int UnregisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, [In] [MarshalAs(UnmanagedType.LPStruct)] Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool isMuted, [In] [MarshalAs(UnmanagedType.LPStruct)] Guid ctx);
        [PreserveSig] int GetMute(out bool isMuted);
    }

    private static IAudioSessionManager2? ActivateManager(IMMDevice device)
    {
        IntPtr raw = IntPtr.Zero;
        try
        {
            var hr = device.Activate(ManagerIid, CLSCTX_ALL, IntPtr.Zero, out raw);
            if (hr < 0 || raw == IntPtr.Zero) return null;
            return Marshal.GetObjectForIUnknown(raw) as IAudioSessionManager2;
        }
        catch { return null; }
        finally
        {
            if (raw != IntPtr.Zero) Marshal.Release(raw);
        }
    }

    private static int MuteSessionsByPids(HashSet<int> pids)
    {
        int muted = 0;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(0 /*eRender*/, DEVICE_STATE_ACTIVE, out var collection) != 0)
                return 0;
            if (collection is null) return 0;

            collection.GetCount(out uint deviceCount);
            for (uint i = 0; i < deviceCount; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                if (device is null) continue;
                var manager = ActivateManager(device);
                if (manager is null) continue;

                manager.GetSessionEnumerator(out var sessionEnum);
                if (sessionEnum is null) continue;
                sessionEnum.GetCount(out int count);
                for (int k = 0; k < count; k++)
                {
                    if (sessionEnum.GetSession(k, out var control) != 0) continue;
                    if (control is null) continue;

                    uint sessionPid = 0;
                    if (control is IAudioSessionControl2 control2)
                    {
                        try { control2.GetProcessId(out sessionPid); } catch { }
                    }

                    if (sessionPid == 0 || !pids.Contains((int)sessionPid)) continue;
                    if (control is ISimpleAudioVolume volume)
                    {
                        try
                        {
                            if (volume.SetMute(true, ManagerIid) == 0) muted++;
                        }
                        catch { }
                    }
                }
            }
        }
        catch
        {
        }
        return muted;
    }
}