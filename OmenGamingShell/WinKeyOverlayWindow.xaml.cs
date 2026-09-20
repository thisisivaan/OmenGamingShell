using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
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
    private static readonly Brush BatteryGreen = CreateFrozenBrush("#38D878");
    private static readonly Brush BatteryYellow = CreateFrozenBrush("#FFC928");
    private static readonly Brush BatteryRed = CreateFrozenBrush("#FF003C");
    private readonly MainWindow _shell;
    private IReadOnlyList<TaskWindowEntry> _windows = Array.Empty<TaskWindowEntry>();
    private IntPtr _previousForeground;
    private bool _suppressRestore;
    private bool _overlayOpen;
    private bool _lastNetworkAvailable = true;
    private int _statusTick;
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _captureDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public WinKeyOverlayWindow(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        _dashboardTimer.Tick += (_, _) => RefreshDashboardSafe();
        _statusTimer.Tick += (_, _) => RefreshStatusBar();
        _captureDebounce.Tick += (_, _) => { _captureDebounce.Stop(); RefreshCaptures(); };
        NotificationCenter.Updated += OnNotificationCenterUpdated;
        Closed += (_, _) => NotificationCenter.Updated -= OnNotificationCenterUpdated;
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
        RefreshStatusBar();
        UpdatePerfModeDisplay();
        InstalledAppsCatalog.WarmUp();

        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Clear();
            SearchBox.Focus();
            UpdateLayout();
            RefreshCaptures();
            _dashboardTimer.Start();
            _statusTimer.Start();
        }, DispatcherPriority.Loaded);
    }

    public void CloseOverlay(bool restoreForeground = true)
    {
        if (!_overlayOpen) return;
        _overlayOpen = false;
        _dashboardTimer.Stop();
        _statusTimer.Stop();
        _captureDebounce.Stop();

        SearchBox.Text = string.Empty;
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
                           key == Key.Decimal ||
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
        if (FindAncestor<Border>(source, border => border.Name == "Dashboard" || border.Name == "SearchBorder" || border.Name == "StatusBorder") is not null) return;
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
        var dpiX = 96u;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out _) != 0)
            dpiX = 96;
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
            uint curThread;
            try { curThread = GetCurrentThreadId(); }
            catch (EntryPointNotFoundException) { curThread = GetWindowThreadProcessId(GetForegroundWindow(), out _); }
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
        _captureDebounce.Stop();
        _captureDebounce.Start();
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
            case GameEntry game when _shell.VisibleGames.Contains(game):
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
        if (!_overlayOpen || VolumeValueText is null) return;
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
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.OpenGameStore();
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

    private void RefreshStatusBar()
    {
        if (!_overlayOpen) return;
        try
        {
            var now = DateTime.Now;
            ClockText.Text = now.ToString("HH:mm");
            DateText.Text = now.ToString("dddd, dd MMMM").ToUpperInvariant();
            RefreshBattery();
            if (_statusTick++ % 5 == 0) RefreshConnectivity();
        }
        catch { }
    }

    private void RefreshBattery()
    {
        var percent = PowerStatus.BatteryPercent();
        if (PowerStatus.IsCharging())
        {
            BatteryFill.Width = percent is null ? 0 : 16d * percent.Value / 100d;
            BatteryFill.Background = BatteryGreen;
            ChargingIcon.Visibility = Visibility.Visible;
            BatteryPercentText.Text = percent is null ? string.Empty : $"{percent.Value}%";
            return;
        }
        ChargingIcon.Visibility = Visibility.Collapsed;
        if (percent is null)
        {
            BatteryFill.Width = 0;
            BatteryPercentText.Text = string.Empty;
            return;
        }
        BatteryFill.Width = 16d * percent.Value / 100d;
        BatteryFill.Background = percent > 50 ? BatteryGreen : percent > 20 ? BatteryYellow : BatteryRed;
        BatteryPercentText.Text = $"{percent.Value}%";
    }

    private void RefreshConnectivity()
    {
        try
        {
            var available = NetworkInterface.GetIsNetworkAvailable();
            if (available != _lastNetworkAvailable)
            {
                _lastNetworkAvailable = available;
                WifiStatusIcon.Opacity = available ? 1 : 0.35;
            }
        }
        catch { }
        BluetoothStatusIcon.Opacity = MainWindow.IsBluetoothRadioAvailable() ? 1 : 0.35;
    }

    private void WifiButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.ShowConnectionsFromOverlay(true);
    }

    private void BluetoothButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.ShowConnectionsFromOverlay(false);
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
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
        var covers = _shell.VisibleGames
            .Where(g => !g.IsHidden && !string.IsNullOrWhiteSpace(g.Cover) && File.Exists(g.Cover))
            .Select(g => g.Cover!)
            .ToList();
        var cells = new[] { StoreCover1, StoreCover2, StoreCover3, StoreCover4, StoreCover5, StoreCover6 };
        if (covers.Count == 0)
        {
            foreach (var cell in cells)
                cell.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#241414"));
            _storeCollagePool = null;
            return;
        }
        if (_storeCollagePool is not null && _storeCollagePool.SequenceEqual(covers)) return;
        _storeCollagePool = covers;
        var pool = new List<string>(covers);
        var random = new Random();
        foreach (var cell in cells)
        {
            var chosen = pool[random.Next(pool.Count)];
            var image = LoadLocalImage(chosen);
            cell.Background = image is not null
                ? new ImageBrush(image) { Stretch = Stretch.UniformToFill }
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#241414"));
        }
    }

    private List<string>? _storeCollagePool;

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
        var mode = (_shell.CurrentPerformanceMode ?? "Balanced").ToUpperInvariant();
        PerfModeText.Text = mode;
        var accentBrush = (Brush)FindResource("AccentBrush");
        var defaultBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        foreach (var btn in FindVisualChildren<Button>(this))
        {
            if (btn.Tag is string tag && tag is "Ultimate" or "Balanced" or "Eco")
            {
                var label = tag.ToUpperInvariant();
                btn.Background = label == mode ? accentBrush : defaultBrush;
            }
        }
    }

    private static ImageSource? LoadLocalImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (_coverCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.None;
            image.DecodePixelWidth = 256;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _coverCache[path] = image;
            return image;
        }
        catch { return null; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> _coverCache = new();

    private void RefreshCaptures()
    {
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
        uint curThread;
        try { curThread = GetCurrentThreadId(); }
        catch (EntryPointNotFoundException) { curThread = GetWindowThreadProcessId(foreground, out _); }
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

    private static bool IsTaskSwitcherWindow(IntPtr handle)
    {
        bool minimized = IsIconic(handle);
        if (!IsWindowVisible(handle) && !minimized) return false;
        if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 &&
            cloaked == 1 && !minimized) return false;
        var extendedStyle = GetWindowLong(handle, GWL_EXSTYLE);
        if ((extendedStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
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
            if (!IsTaskSwitcherWindow(handle)) return true;
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

    private static T? FindAncestor<T>(DependencyObject? current, Func<T, bool> predicate) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match && predicate(match)) return match;
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
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)]
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
    private const int WS_EX_NOACTIVATE = 0x08000000;

    #endregion
}
