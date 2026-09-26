using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class ToastOverlay : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private Action? _onClick;

    public ToastOverlay()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => HideToast();
        _topmostTimer.Tick += (_, _) => EnsureTopmost();
    }

    public void ShowToast(string message, Action? onClick = null)
    {
        ToastText.Text = message.ToUpperInvariant();
        _onClick = onClick;

        if (!IsVisible)
        {
            Show();
            UpdateLayout();
        }
        PositionAtTopCenter();
        EnsureTopmost();
        _hideTimer.Stop();
        _hideTimer.Start();
        _topmostTimer.Start();
    }

    private void EnsureTopmost()
    {
        if (VolumeBrightnessOsd.IsDisplaying) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private static void SetWindowLong(IntPtr window, int index, int value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(window, index, new IntPtr(value));
        else
            SetWindowLong32(window, index, value);
    }

    private void PositionAtTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 48;
    }

    private void ToastOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var action = _onClick;
        HideToast();
        action?.Invoke();
    }

    private void HideToast()
    {
        _hideTimer.Stop();
        _topmostTimer.Stop();
        _onClick = null;
        Hide();
    }
}