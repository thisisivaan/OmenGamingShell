using System.Windows;
using System.Windows.Input;

namespace OmenGamingShell;

public partial class InGameHomeOverlay : Window
{
    public event Action? ReturnRequested;

    public InGameHomeOverlay() => InitializeComponent();

    public void Open(string gameName, IReadOnlyList<GameEntry>? games)
    {
        RunningGameText.Text = $"{gameName.ToUpperInvariant()} IS RUNNING";
        OverlayGames.ItemsSource = games?.Take(5).ToList();
        Show();
        Activate();
        Focus();
    }

    public void ReturnToGame()
    {
        Hide();
        ReturnRequested?.Invoke();
    }

    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.BrowserBack)
        {
            ReturnToGame();
            e.Handled = true;
        }
    }
}
