using System.Windows;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class VolumeBrightnessOsd : Window
{
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

        if (!IsVisible) PositionNearBottom();
        Show();
        UpdateLayout();

        var trackWidth = OsdCard.ActualWidth - 76;
        if (trackWidth <= 0) trackWidth = 200;
        OsdBar.Width = trackWidth * Math.Clamp(percent, 0, 100) / 100.0;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void PositionNearBottom()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height - Height - 64;
    }
}