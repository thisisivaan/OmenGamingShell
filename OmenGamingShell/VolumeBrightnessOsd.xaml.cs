using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class VolumeBrightnessOsd : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    public VolumeBrightnessOsd()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
    }

    public void ShowOsd(int percent, bool isBrightness)
    {
        OsdIcon.Text = isBrightness ? "\uE706" : "\uE767";
        OsdLabel.Text = isBrightness ? "BRIGHTNESS" : "VOLUME";
        OsdPercent.Text = $"{percent}%";

        if (!IsVisible) PositionAtTopCenter();
        Show();
        UpdateLayout();

        var trackWidth = OsdCard.ActualWidth - 76;
        if (trackWidth <= 0) trackWidth = 200;
        OsdBar.Width = trackWidth * Math.Clamp(percent, 0, 100) / 100.0;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void PositionAtTopCenter()
    {
        var area = SystemParameters.WorkArea;
        if (GetCursorPos(out var cursor))
        {
            var handle = MonitorFromPoint(cursor, 0x00000002 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (handle != IntPtr.Zero && GetMonitorInfo(handle, ref info))
            {
                area = new Rect(info.WorkArea.Left, info.WorkArea.Top,
                    info.WorkArea.Right - info.WorkArea.Left, info.WorkArea.Bottom - info.WorkArea.Top);
            }
        }
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 24;
    }
}