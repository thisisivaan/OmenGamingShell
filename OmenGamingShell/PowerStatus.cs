using System.Runtime.InteropServices;

namespace OmenGamingShell;

public static class PowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static bool IsCharging()
    {
        return GetSystemPowerStatus(out var status) && status.AcLineStatus == 1;
    }

    public static int? BatteryPercent()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryLifePercent == byte.MaxValue) return null;
        return Math.Clamp((int)status.BatteryLifePercent, 0, 100);
    }
}