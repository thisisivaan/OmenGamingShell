using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class WinKeyOverlayWindow : Window
{
    private readonly MainWindow _shell;
    private IReadOnlyList<TaskWindowEntry> _windows = Array.Empty<TaskWindowEntry>();
    private IntPtr _previousForeground;
    private bool _suppressRestore;
    private bool _overlayOpen;
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public WinKeyOverlayWindow(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        _dashboardTimer.Tick += (_, _) => RefreshDashboardSafe();
        NotificationCenter.Updated += OnNotificationCenterUpdated;
    }

    public void OpenOverlay()
    {
        if (_overlayOpen) { CloseOverlay(); return; }
        _overlayOpen = true;
        _suppressRestore = false;

        CapturePreviousForeground();
        PositionOnCurrentMonitor();

        Show();
        Activate();
        Topmost = true;

        PopulateWindows();
        RefreshApps(string.Empty);
        RefreshDashboardSafe();
        UpdatePerfModeDisplay();
        _ = Task.Run(() => { var _ = InstalledAppsCatalog.GetApps(); });

        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Clear();
            SearchBox.Focus();
            UpdateLayout();
            RefreshCaptures();
            _dashboardTimer.Start();
        }, DispatcherPriority.Loaded);
    }

    public void CloseOverlay(bool restoreForeground = true)
    {
        if (!_overlayOpen) return;
        _overlayOpen = false;
        _dashboardTimer.Stop();

        SearchBox.Text = string.Empty;
        WindowList.ItemsSource = null;
        AppsList.ItemsSource = null;
        WindowsSection.Visibility = Visibility.Collapsed;
        AppsSection.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;

        Hide();
        WindowList.ItemsSource = null;
        AppsList.ItemsSource = null;
        WindowsSection.Visibility = Visibility.Collapsed;
        AppsSection.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;

        Hide();

        if (restoreForeground && !_suppressRestore)
            RestorePreviousForeground();
    }

    public void ForceClose()
    {
        _suppressRestore = true;
        CloseOverlay(false);
    }

    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_overlayOpen) return;
        if (SearchBox.IsKeyboardFocused) return;
        var key = e.Key;
        bool isPrintable = key is >= Key.A and <= Key.Z ||
                           key is >= Key.D0 and <= Key.D9 ||
                           key is >= Key.NumPad0 and <= Key.NumPad9 ||
                           key == Key.Space || key == Key.Oem1 || key == Key.Oem2 ||
                           key == Key.Oem3 || key == Key.Oem4 || key == Key.Oem5 ||
                           key == Key.Oem6 || key == Key.Oem7 || key == Key.Oem8 ||
                           key == Key.Oem102 || key == Key.OemPeriod || key == Key.OemComma ||
                           key == Key.OemMinus || key == Key.OemPlus || key == Key.OemQuestion ||
                           key == Key.OemQuotes || key == Key.OemSemicolon ||
                           key == Key.Back || key == Key.Delete;
        if (isPrintable)
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
    }

    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseOverlay();
            e.Handled = true;
        }
    }

    private void RootGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, RootGrid))
            CloseOverlay();
    }

    private void ContentPanel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<Button>(source) is not null) return;
        if (e.OriginalSource is TextBox) return;
        if (FindAncestor<Border>(source) is { } inner && (inner.Name == "Dashboard" || inner.Name == "SearchBorder")) return;
        CloseOverlay();
        e.Handled = true;
    }

    private void PositionOnCurrentMonitor()
    {
        var monitor = IntPtr.Zero;
        if (_previousForeground != IntPtr.Zero)
            monitor = MonitorFromWindow(_previousForeground, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            GetCursorPos(out var cursor);
            monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        }
        if (monitor == IntPtr.Zero)
            monitor = MonitorFromWindow(new WindowInteropHelper(_shell).Handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            return;
        }
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out var _);
        var scaleX = dpiX / 96.0;
        Left = info.Work.Left / scaleX;
        Top = info.Work.Top / scaleX;
        Width = (info.Work.Right - info.Work.Left) / scaleX;
        Height = (info.Work.Bottom - info.Work.Top) / scaleX;
    }

    private void CapturePreviousForeground()
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != new WindowInteropHelper(_shell).Handle && fg != new WindowInteropHelper(this).Handle)
            _previousForeground = fg;
    }

    private void RestorePreviousForeground()
    {
        if (_previousForeground == IntPtr.Zero) return;
        var target = _previousForeground;
        _previousForeground = IntPtr.Zero;
        try
        {
            if (!IsWindow(target)) return;
            ShowWindow(target, SW_RESTORE);
            BringWindowToTop(target);
            var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var targetThread = GetWindowThreadProcessId(target, out _);
            var curThread = GetCurrentThreadId();
            if (fgThread != curThread) AttachThreadInput(curThread, fgThread, true);
            if (targetThread != curThread) AttachThreadInput(curThread, targetThread, true);
            SetForegroundWindow(target);
            if (targetThread != curThread) AttachThreadInput(curThread, targetThread, false);
            if (fgThread != curThread) AttachThreadInput(curThread, fgThread, false);
        }
        catch { }
    }

    private void PopulateWindows()
    {
        var overlayHandle = new WindowInteropHelper(this).Handle;
        var shellHandle = new WindowInteropHelper(_shell).Handle;
        _windows = GetTaskWindows(overlayHandle, shellHandle);
        WindowList.ItemsSource = _windows;
        WindowsSection.Visibility = _windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = (_windows.Count == 0 && AppsSection.Visibility != Visibility.Visible)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshApps(string query)
    {
        var games = _shell.VisibleGames;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var results = new List<object>();
            var installedApps = InstalledAppsCatalog.GetApps();
            results.AddRange(games.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            results.AddRange(installedApps.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            var unique = results
                .GroupBy(item => item switch
                {
                    GameEntry game => game.Name,
                    AppEntry app => app.Name,
                    _ => item.ToString()
                }, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item switch { GameEntry game => game.Name, AppEntry app => app.Name, _ => "" },
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            AppsList.ItemsSource = unique;
            AppsSection.Visibility = unique.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            AppsList.ItemsSource = null;
            AppsSection.Visibility = Visibility.Collapsed;
        }
        EmptyHint.Visibility = (WindowsSection.Visibility != Visibility.Visible && AppsSection.Visibility != Visibility.Visible)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_overlayOpen) return;
        var query = SearchBox.Text.Trim();
        RefreshApps(query);
        if (string.IsNullOrWhiteSpace(query))
        {
            WindowList.ItemsSource = _windows;
            WindowsSection.Visibility = _windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            var filtered = _windows
                .Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            w.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            WindowList.ItemsSource = filtered;
            WindowsSection.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        EmptyHint.Visibility = (WindowsSection.Visibility != Visibility.Visible && AppsSection.Visibility != Visibility.Visible)
            ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => RefreshCaptures(), DispatcherPriority.Loaded);
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66FF003C"));
    }

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14FFFFFF"));
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void TaskWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskWindowEntry window }) return;
        _suppressRestore = true;
        CloseOverlay(false);
        ActivateWindow(window);
    }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: not null } button) return;
        CloseOverlay();
        switch (button.Tag)
        {
            case GameEntry game:
                _shell.LaunchGame(game);
                break;
            case AppEntry app:
                app.Launch();
                break;
        }
    }

    private void PerformanceMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        _shell.ApplyPerformanceModeFromOverlay(mode);
        UpdatePerfModeDisplay();
    }

    private void MediaPlay_Click(object sender, RoutedEventArgs e) => _ = MediaService.TogglePlayPauseAsync();

    private void MediaPrev_Click(object sender, RoutedEventArgs e) => _ = MediaService.PreviousAsync();

    private void MediaNext_Click(object sender, RoutedEventArgs e) => _ = MediaService.NextAsync();

    private bool _volumeSliderSyncing;

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var percent = (int)Math.Round(Math.Clamp(e.NewValue, 0, 100));
        VolumeValueText.Text = percent.ToString();
        if (_volumeSliderSyncing) return;
        SystemAudio.SetVolume(percent);
        _shell.ShowVolumeOsd(percent);
    }

    private void MediaMute_Click(object sender, RoutedEventArgs e)
    {
        SystemAudio.SetMuted(!SystemAudio.IsMuted());
        RefreshVolumeState();
    }

    private void RefreshVolumeState()
    {
        var percent = (int)Math.Round(SystemAudio.GetVolume());
        var muted = SystemAudio.IsMuted();
        _volumeSliderSyncing = true;
        VolumeSlider.Value = percent;
        _volumeSliderSyncing = false;
        VolumeValueText.Text = percent.ToString();
        MediaMuteButton.Content = muted ? "\uE74F" : "\uE767";
    }

    private void StoreClick(object sender, MouseButtonEventArgs e)
    {
        var game = FeaturedStoreGame();
        if (game is null) return;
        CloseOverlay();
        _shell.LaunchGame(game);
    }

    private void NotificationClick(object sender, MouseButtonEventArgs e)
    {
        CloseOverlay();
        _shell.ShowNotificationCenter();
    }

    private void RefreshDashboardSafe()
    {
        if (!_overlayOpen) return;
        try
        {
            _ = RefreshMediaAsync();
            RefreshNotifications();
            RefreshStoreFeatured();
            RefreshVolumeState();
        }
        catch { }
    }

    private void OnNotificationCenterUpdated()
    {
        if (_overlayOpen)
            Dispatcher.BeginInvoke(RefreshNotifications, DispatcherPriority.Background);
    }

    private void RefreshNotifications()
    {
        var latest = NotificationCenter.Items.FirstOrDefault();
        NotificationTitle.Text = latest is not null ? latest.Title : "No notifications";
        NotificationMessage.Text = latest?.Message ?? string.Empty;
    }

    private void RefreshStoreFeatured()
    {
        var game = FeaturedStoreGame();
        if (game is null)
        {
            StoreGameName.Text = "Nothing featured";
            StoreGameDev.Text = string.Empty;
            StoreArtInner.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10FFFFFF"));
            return;
        }
        StoreGameName.Text = game.Name;
        StoreGameDev.Text = string.Join("  •  ", new[] { game.Developer, game.Publisher }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var art = LoadLocalImage(game.Cover);
        StoreArtInner.Background = art is not null
            ? new ImageBrush(art)
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10FFFFFF"));
    }

    private GameEntry? FeaturedStoreGame()
    {
        var games = _shell.VisibleGames.Where(g => !g.IsHidden).ToList();
        if (games.Count == 0) return null;
        var favorite = games.FirstOrDefault(g => g.IsFavorite);
        if (favorite is not null) return favorite;
        var rated = games.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.Rating));
        if (rated is not null) return rated;
        var recentlyPlayed = games
            .Where(g => g.LastPlayedUtc.HasValue)
            .OrderByDescending(g => g.LastPlayedUtc)
            .FirstOrDefault();
        if (recentlyPlayed is not null) return recentlyPlayed;
        return games[Math.Min(games.Count - 1, new Random().Next(games.Count))];
    }

    private async Task RefreshMediaAsync()
    {
        var media = await MediaService.GetCurrentMediaAsync();
        BitmapImage? albumArt = null;
        if (media?.Thumbnail is not null)
        {
            try
            {
                using var stream = await media.Thumbnail.OpenReadAsync();
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = stream.AsStreamForRead();
                bi.EndInit();
                bi.Freeze();
                albumArt = bi;
            }
            catch { }
        }
        _ = Dispatcher.BeginInvoke(() =>
        {
            MediaTitle.Text = media is not null ? media.Title : "No media";
            MediaArtist.Text = media?.Artist ?? string.Empty;
            MediaPlayButton.Content = media is { IsPlaying: true } ? "\uEDB4" : "\uEDB8";
            MediaPlayButton.Visibility = media is null ? Visibility.Collapsed : Visibility.Visible;
            if (albumArt is not null) MediaArt.Background = new ImageBrush(albumArt);
            else MediaArt.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10FFFFFF"));
        }, DispatcherPriority.Background);
    }

    private void UpdatePerfModeDisplay()
    {
        PerfModeText.Text = (_shell.CurrentPerformanceMode ?? "Balanced").ToUpperInvariant();
    }

    private static ImageSource? LoadLocalImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void RefreshCaptures()
    {
        var nonce = unchecked(DateTime.UtcNow.Ticks);
        foreach (var border in FindVisualChildren<Border>(WindowList)
                     .Where(b => b.Tag is TaskWindowEntry && b.IsVisible))
        {
            var window = (TaskWindowEntry)border.Tag;
            var target = border;
            _ = Task.Run(async () =>
            {
                var image = window.Capture();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_overlayOpen || target.Tag is not TaskWindowEntry) return;
                    target.Background = image is null
                        ? Brushes.Transparent
                        : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                }, DispatcherPriority.Background);
            });
        }
    }

    private static void ActivateWindow(TaskWindowEntry window)
    {
        ShowWindow(window.Handle, SW_RESTORE);
        BringWindowToTop(window.Handle);
        var foreground = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(window.Handle, out _);
        var curThread = GetCurrentThreadId();
        try
        {
            if (fgThread != curThread) AttachThreadInput(curThread, fgThread, true);
            if (targetThread != curThread) AttachThreadInput(curThread, targetThread, true);
            SetForegroundWindow(window.Handle);
            SetFocus(window.Handle);
        }
        finally
        {
            if (targetThread != curThread) AttachThreadInput(curThread, targetThread, false);
            if (fgThread != curThread) AttachThreadInput(curThread, fgThread, false);
        }
    }

    private static bool IsTaskSwitcherWindow(IntPtr handle, IntPtr overlayHandle)
    {
        bool minimized = IsIconic(handle);
        if (!IsWindowVisible(handle) && !minimized) return false;
        if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 &&
            cloaked == 1 && !minimized) return false;
        var extendedStyle = GetWindowLong(handle, GWL_EXSTYLE);
        if ((extendedStyle & WS_EX_TOOLWINDOW) != 0) return false;
        if (!GetWindowRect(handle, out var rectangle) || rectangle.Right - rectangle.Left < 100 ||
            rectangle.Bottom - rectangle.Top < 80) return false;
        var className = new System.Text.StringBuilder(256);
        GetClassName(handle, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
            return false;
        return true;
    }

    private static List<TaskWindowEntry> GetTaskWindows(IntPtr overlayHandle, IntPtr shellHandle)
    {
        var windows = new List<TaskWindowEntry>();
        EnumWindows((handle, _) =>
        {
            if (handle == overlayHandle || handle == shellHandle) return true;
            if (!IsTaskSwitcherWindow(handle, overlayHandle)) return true;
            var length = GetWindowTextLength(handle);
            if (length == 0) return true;
            var title = new System.Text.StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            if (string.IsNullOrWhiteSpace(title.ToString())) return true;
            GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                string? executable = null;
                try { executable = process.MainModule?.FileName; } catch { }
                windows.Add(new TaskWindowEntry
                {
                    Handle = handle,
                    Title = title.ToString(),
                    ProcessName = process.ProcessName.ToUpperInvariant(),
                    ExecutablePath = executable
                });
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    #region Native interop

    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maximumCount);
    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maximumCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);
    [DllImport("user32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int SW_RESTORE = 9;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    #endregion
}
