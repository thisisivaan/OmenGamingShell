using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace OmenGamingShell;

public sealed class SystemStats
{
    public double CpuUsage { get; init; }
    public double RamUsagePercent { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
}

public static class PerformanceMonitor
{
    private static PerformanceCounter? _cpuCounter;
    private static bool _initialized;
    private static SystemStats _latest = new();
    private static readonly object _sync = new();
    private static Timer? _backgroundTimer;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue();
            _backgroundTimer = new Timer(_ => SampleBackground(), null, 0, 3000);
        }
        catch { _cpuCounter = null; }
    }

    private static void SampleBackground()
    {
        try
        {
            var cpu = 0.0;
            try { if (_cpuCounter is not null) { _cpuCounter.NextValue(); Thread.Sleep(250); cpu = _cpuCounter.NextValue(); } } catch { }

            var totalMem = 0.0;
            var usedMem = 0.0;
            try
            {
                var si = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(ref si))
                {
                    totalMem = si.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    usedMem = totalMem * (si.dwMemoryLoad / 100.0);
                }
            }
            catch { }

            var usedGb = Math.Round(usedMem, 1);
            var totalGb = Math.Round(totalMem, 1);
            var snapshot = new SystemStats
            {
                CpuUsage = Math.Round(cpu, 1),
                RamUsagePercent = totalMem > 0 ? Math.Round(usedMem * 100.0 / totalMem, 1) : 0,
                RamUsedGb = usedGb,
                RamTotalGb = totalGb
            };
            lock (_sync) { _latest = snapshot; }
        }
        catch { }
    }

    public static SystemStats Sample()
    {
        if (!_initialized) Init();
        lock (_sync) { return _latest; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>(); }
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
