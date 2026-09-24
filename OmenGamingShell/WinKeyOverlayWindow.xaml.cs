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
        WindowsSection.Visibility = Visibility.Collapsed;

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
        else if (key == Key.Back || key == Key.Delete)
        {
            SearchBox.Focus();
            if (SearchBox.Text.Length > 0)
                SearchBox.Text = SearchBox.Text[..^1];
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }
    }

    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && AppsList.ItemsSource is System.Collections.IList { Count: > 0 })
        {
            LaunchFirstSearchResult();
            e.Handled = true;
        }
    }

    private void Overlay_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_overlayOpen) return;
        if (SearchBox.IsKeyboardFocused) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        SearchBox.Focus();
        var insertAt = SearchBox.CaretIndex;
        SearchBox.Text = SearchBox.Text.Insert(insertAt, e.Text);
        SearchBox.CaretIndex = insertAt + e.Text.Length;
    }

    private void LaunchFirstSearchResult()
    {
        if (!_overlayOpen) return;
        if (AppsList.ItemsSource is not System.Collections.IList list || list.Count == 0) return;
        _suppressRestore = true;
        CloseOverlay(false);
        switch (list[0])
        {
            case GameEntry game when _shell.VisibleGames.Contains(game):
                _shell.LaunchGame(game);
                break;
            case AppEntry app:
                app.Launch();
                break;
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
        if (FindAncestor<Border>(source, border => border.Name is "MediaCard" or "SearchBorder" or "StatusBorder") is not null) return;
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
    }

    private void RefreshApps(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            PopulateFavorites();
            return;
        }
        var games = _shell.VisibleGames;
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
            .Take(5)
            .ToList();
        AppsList.ItemsSource = unique;
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

    private void FavoriteTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: not null } button) return;
        CloseOverlay();
        if (button.Tag is GameEntry game && _shell.VisibleGames.Contains(game))
            _shell.LaunchGame(game);
        else if (button.Tag is AppEntry app)
            app.Launch();
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

    private void RefreshDashboardSafe()
    {
        if (!_overlayOpen) return;
        try
        {
            _ = RefreshMediaAsync();
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
            PerformanceText.Text = _shell.CurrentPerformanceMode.ToUpperInvariant();
            RefreshBattery();
            RefreshShellBadges();
            if (_statusTick++ % 5 == 0) RefreshConnectivity();
        }
        catch { }
    }

    private void RefreshShellBadges()
    {
        try
        {
            var active = DownloadCenterService.ActiveCount;
            if (active > 0)
            {
                OverlayDownloadBadge.Visibility = Visibility.Visible;
                OverlayDownloadBadgeText.Text = active.ToString();
            }
            else
            {
                OverlayDownloadBadge.Visibility = Visibility.Collapsed;
            }

            var errors = ErrorLogStore.LoadLogged().Count;
            if (errors > 0)
            {
                OverlayErrorBadge.Visibility = Visibility.Visible;
                OverlayErrorBadgeText.Text = errors > 99 ? "99+" : errors.ToString();
            }
            else
            {
                OverlayErrorBadge.Visibility = Visibility.Collapsed;
            }
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
        _ = UpdateBluetoothStatusAsync();
    }

    private async Task UpdateBluetoothStatusAsync()
    {
        var available = await MainWindow.IsBluetoothRadioAvailable().ConfigureAwait(true);
        BluetoothStatusIcon.Opacity = available ? 1 : 0.35;
    }

    private void WifiButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.ShowConnectionsFromOverlay(true);
    }

    private void ShellRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.RefreshGamesFromOverlay();
    }

    private void ShellDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.OpenDownloadCenterFromOverlay();
    }

    private void ShellSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.OpenSettingsFromOverlay();
    }

    private void ShellErrorButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.OpenErrorLogFromOverlay();
    }

    private void ShellDesktopButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.SwitchDesktopFromOverlay();
    }

    private void ShellPowerButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.OpenPowerOptionsFromOverlay();
    }

    private void ShellHomeButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressRestore = true;
        CloseOverlay(false);
        _shell.ShowGameLibraryFromOverlay();
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
        // Overlay no longer surfaces notifications; kept as a no-op safety latch.
    }

    private async Task RefreshMediaAsync()
    {
        var media = await MediaService.GetCurrentMediaAsync();
        _ = Dispatcher.BeginInvoke(() =>
        {
            MediaTitle.Text = media is not null ? media.Title : "Open Spotify";
            MediaArtist.Text = media?.Artist ?? "Tap to open Spotify";
            MediaPlayButton.Content = media is { IsPlaying: true } ? "\uEDB4" : "\uEDB8";
            MediaPlayButton.Visibility = media is null ? Visibility.Collapsed : Visibility.Visible;
            MediaPrevButton.Visibility = media is null ? Visibility.Collapsed : Visibility.Visible;
            MediaNextButton.Visibility = media is null ? Visibility.Collapsed : Visibility.Visible;
        }, DispatcherPriority.Background);
    }

    private void MediaTrackHost_Click(object sender, MouseButtonEventArgs e)
    {
        var info = Task.Run(() => MediaService.GetCurrentMediaAsync()).Result;
        if (info?.AppName is { Length: > 0 } appId)
        {
            try
            {
                Process.Start(new ProcessStartInfo($"shell:AppsFolder\\{appId}") { UseShellExecute = true });
                return;
            }
            catch { }
        }
        _shell.OpenSpotify();
    }

    private static readonly string[] FavoritesPriorityNames =
    {
        "chrome", "edge", "firefox", "discord", "spotify", "steam", "epic",
        "whatsapp", "telegram", "notepad", "terminal", "settings", "file explorer",
        "email", "mail", "calculator", "photos", "word", "excel", "powerpoint"
    };

    private void PopulateFavorites()
    {
        var apps = InstalledAppsCatalog.GetApps();
        var favorites = apps
            .OrderByDescending(a =>
            {
                var name = a.Name!.ToLowerInvariant();
                for (var i = 0; i < FavoritesPriorityNames.Length; i++)
                    if (name.Contains(FavoritesPriorityNames[i], StringComparison.OrdinalIgnoreCase))
                        return FavoritesPriorityNames.Length - i;
                return 0;
            })
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        AppsList.ItemsSource = favorites;
    }

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
                        : new ImageBrush(image) { Stretch = Stretch.Uniform };
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
