using System.Windows;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class BatteryCriticalOverlay : Window
{
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public BatteryCriticalOverlay()
    {
        InitializeComponent();
        _monitorTimer.Tick += (_, _) => MonitorPower();
    }

    public void ShowOverlay(int? percent, int threshold = 5)
    {
        var firstShow = !IsVisible;
        ThresholdText.Text = $"BATTERY LOWER THAN {threshold}%";
        UpdatePercent(percent);
        Show();
        if (firstShow)
        {
            Activate();
            Focus();
        }
        _monitorTimer.Start();
    }

    private void UpdatePercent(int? percent)
    {
        BatteryPercentText.Text = percent is null ? "--% REMAINING" : $"{percent}% REMAINING";
    }

    private void MonitorPower()
    {
        var percent = PowerStatus.BatteryPercent();
        if (percent is not null) BatteryPercentText.Text = $"{percent}% REMAINING";
        if (PowerStatus.IsCharging()) Dismiss();
    }

    public void Dismiss()
    {
        _monitorTimer.Stop();
        Hide();
    }

    public void ForceClose()
    {
        _monitorTimer.Stop();
        Close();
    }
}