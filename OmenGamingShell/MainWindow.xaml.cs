using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class MainWindow : Window
{
    private static readonly Brush BatteryGreen = CreateFrozenBrush("#38D878");
    private static readonly Brush BatteryYellow = CreateFrozenBrush("#FFC928");
    private static readonly Brush BatteryRed = CreateFrozenBrush("#FF003C");
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _cursorHideTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _coverExitTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private readonly DispatcherTimer _wifiScanTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _deviceScanTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _taskViewHotCornerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _applicationEdgeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ControllerInput _controller = new();
    private MetadataSettings _metadataSettings = new();
    private InputSettings _inputSettings = new();
    private ControllerProfile _editingControllerProfile = new();
    private string _appliedControllerName = string.Empty;
    private ShellKeyboardGuard? _keyboardGuard;
    private readonly GameSessionManager _gameSession = new();
    private string? _pendingCorrectedCover;
    private string? _pendingCorrectedBackground;
    private IReadOnlyList<GameEntry> _allGames = Array.Empty<GameEntry>();
    private IReadOnlyList<GameEntry> _applications = Array.Empty<GameEntry>();
    private bool _applicationsSelected;
    private bool _homeSelected;
    private bool _homeHoverLocked;
    private GameEntry? _selectedDetailsGame;
    private bool _lastControllerConnected;
    private string _activeLibraryFilter = "All";
    private string _activeApplicationFilter = "All";
    private IReadOnlyList<GameEntry> _applicationResults = Array.Empty<GameEntry>();
    private int _applicationPage;
    private const int ApplicationsPerPage = 25;
    private int _applicationEdgeDirection;
    private bool _applicationEdgeReady = true;
    private HwndSource? _windowSource;
    private string? _lastHomeSelection;
    private readonly List<IntPtr> _taskThumbnails = new();
    private readonly List<IntPtr> _homeTaskThumbnails = new();
    private WifiNetwork? _pendingWifiNetwork;
    private IReadOnlyList<BluetoothDevice> _cachedBluetoothDevices = Array.Empty<BluetoothDevice>();
    private bool _wifiScanInProgress;
    private bool _deviceScanInProgress;
    private Button? _mouseFocusedButton;
    private Button? _mouseSuppressedButton;

    public MainWindow()
    {
        InitializeComponent();
        PreviewTextInput += MainWindow_PreviewTextInput;
        _controller.Command += HandleControllerCommand;
        _controller.AcceptHeld += HandleControllerAcceptHeld;
        _controller.StateChanged += UpdateControllerCalibrationDisplay;
        _controller.GuidePressed += HandleGuideButton;
        _gameSession.StateChanged += HandleGameSessionStateChanged;
        Cursor = Cursors.None;
        _cursorHideTimer.Tick += (_, _) =>
        {
            Cursor = Cursors.None;
            _cursorHideTimer.Stop();
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _notificationTimer.Tick += (_, _) => { _notificationTimer.Stop(); NotificationToast.Visibility = Visibility.Collapsed; };
        _coverExitTimer.Tick += (_, _) =>
        {
            _coverExitTimer.Stop();
            var coverActive = FindVisualChildren<Button>(GamesList)
                .Any(button => button.IsVisible && (button.IsMouseOver || button.IsKeyboardFocusWithin));
            if (coverActive) return;
            HideGameBackground();
            if (!_applicationsSelected)
            {
                QuickLaunchHost.Visibility = Visibility.Visible;
                PerformanceModeHost.Visibility = Visibility.Visible;
            }
        };
        _wifiScanTimer.Tick += async (_, _) =>
        {
            if (ConnectionsOverlay.Visibility == Visibility.Visible &&
                WifiConnectionsPanel.Visibility == Visibility.Visible)
                await RefreshConnectionsAsync(true, false);
            else
                _wifiScanTimer.Stop();
        };
        _deviceScanTimer.Tick += async (_, _) => await RefreshConnectedDevicesAsync();
        _taskViewHotCornerTimer.Tick += (_, _) =>
        {
            _taskViewHotCornerTimer.Stop();
            if (TaskSwitcherOverlay.Visibility != Visibility.Visible) ShowTaskSwitcher();
        };
        _applicationEdgeTimer.Tick += (_, _) =>
        {
            _applicationEdgeTimer.Stop();
            ChangeApplicationPage(_applicationEdgeDirection);
        };
        _clockTimer.Start();
        UpdateClock();
        LoadInputSettings();
        LoadMetadataSettings();
        LoadGames();
        RefreshErrorLog();
        _ = RefreshConnectedDevicesAsync();
        _deviceScanTimer.Start();
        _ = PreloadBluetoothDevicesAsync();
        Loaded += async (_, _) =>
        {
            if (_keyboardGuard is null)
            {
                var windowHandle = new WindowInteropHelper(this).Handle;
                _keyboardGuard = new ShellKeyboardGuard(windowHandle);
                _windowSource = HwndSource.FromHwnd(windowHandle);
                _windowSource?.AddHook(WindowMessageHook);
                _keyboardGuard.WindowsKeyPressed += () => Dispatcher.BeginInvoke(HandleWindowsKey);
                _keyboardGuard.AltTabPressed += () => Dispatcher.BeginInvoke(ShowTaskSwitcher);
                _keyboardGuard.AltReleased += () => Dispatcher.BeginInvoke(ActivateFocusedTaskWindow);
            }
            await RunBootAnimationAsync();
            ShowHomePage();
            if (GamesList.ItemsSource is IReadOnlyList<GameEntry> games)
            {
                await MetadataEnrichmentService.EnrichAsync(games);
                DisplayGames(games);
                ShowHomePage();
            }
        };
    }

    private async Task RefreshConnectedDevicesAsync()
    {
        if (_deviceScanInProgress) return;
        _deviceScanInProgress = true;
        try { ConnectedDevicesList.ItemsSource = await Task.Run(ConnectedDeviceScanner.Scan); }
        catch { ConnectedDevicesList.ItemsSource = Array.Empty<ConnectedDeviceEntry>(); }
        finally { _deviceScanInProgress = false; }
    }

    private void ErrorLog_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PowerOverlay.Visibility == Visibility.Visible) ClosePowerOptions();
        if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        RefreshErrorLog();
        ErrorLogOverlay.Visibility = Visibility.Visible;
        ErrorLogOverlay.UpdateLayout();
        ErrorLogButton.Tag = "OverlayOpen";
        Keyboard.Focus(NewErrorText);
    }

    private void CloseErrorLog_Click(object sender, RoutedEventArgs e) => CloseErrorLog();

    private void CloseErrorLog()
    {
        ErrorLogOverlay.Visibility = Visibility.Collapsed;
        ErrorLogButton.Tag = null;
        NewErrorText.Clear();
    }

    private void LogError_Click(object sender, RoutedEventArgs e)
    {
        var message = NewErrorText.Text.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            ShowNotification("Describe the error before logging it");
            return;
        }
        ErrorLogStore.Log(message);
        NewErrorText.Clear();
        RefreshErrorLog();
        ShowNotification("Error added to errors-logged.json");
    }

    private void NewErrorText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        LogError_Click(sender, e);
    }

    private void MarkErrorFixed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShellErrorEntry entry }) return;
        ErrorLogStore.MarkFixed(entry.Id);
        RefreshErrorLog();
        ShowNotification("Error moved to fixed-errors.json");
    }

    private void RefreshErrorLog()
    {
        var entries = ErrorLogStore.LoadLogged();
        ErrorLogList.ItemsSource = entries;
        ErrorCountText.Text = entries.Count > 99 ? "99+" : entries.Count.ToString();
        ErrorCountBadge.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunBootAnimationAsync()
    {
        UpdateLayout();
        var origin = OmenBrand.TransformToAncestor(this).Transform(new Point(0, 0));
        BrandTranslation.X = (ActualWidth - OmenBrand.ActualWidth) / 2d - origin.X;
        BrandTranslation.Y = (ActualHeight - OmenBrand.ActualHeight) / 2d - origin.Y;
        BootBrandScale.ScaleX = 1.8;
        BootBrandScale.ScaleY = 1.8;

        await AnimateAsync(OmenBrand, UIElement.OpacityProperty, 0, 1, 450);
        await Task.Delay(700);

        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var moveX = AnimateAsync(BrandTranslation, TranslateTransform.XProperty,
            BrandTranslation.X, 0, 1500, easing);
        var moveY = AnimateAsync(BrandTranslation, TranslateTransform.YProperty,
            BrandTranslation.Y, 0, 1500, easing);
        var scaleX = AnimateAsync(BootBrandScale, ScaleTransform.ScaleXProperty, 1.8, 1, 1500, easing);
        var scaleY = AnimateAsync(BootBrandScale, ScaleTransform.ScaleYProperty, 1.8, 1, 1500, easing);
        await Task.Delay(800);
        var reveal = Task.WhenAll(
            AnimateAsync(GameLibraryHeaderHost, UIElement.OpacityProperty, 0, 1, 700),
            AnimateAsync(ApplicationsHeader, UIElement.OpacityProperty, 0, 1, 700),
            AnimateAsync(ShellTopStatus, UIElement.OpacityProperty, 0, 1, 700),
            AnimateAsync(LibraryArea, UIElement.OpacityProperty, 0, 1, 700),
            AnimateAsync(ShellControls, UIElement.OpacityProperty, 0, 1, 700));
        await Task.WhenAll(moveX, moveY, scaleX, scaleY, reveal);
        LibraryArea.IsHitTestVisible = true;
    }

    private static Task AnimateAsync(DependencyObject target, DependencyProperty property,
        double from, double to, int milliseconds, IEasingFunction? easing = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            target.SetValue(property, to);
            completion.TrySetResult();
        };
        if (target is UIElement element)
            element.BeginAnimation(property, animation);
        else if (target is Animatable animatable)
            animatable.BeginAnimation(property, animation);
        else
            throw new InvalidOperationException("The animation target is not animatable.");
        return completion.Task;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_mouseSuppressedButton is not null)
        {
            _mouseSuppressedButton.IsHitTestVisible = true;
            _mouseSuppressedButton = null;
        }
        ShowCursorForMouseActivity();
        var windowPoint = e.GetPosition(this);
        var inTaskViewCorner = windowPoint.X >= Math.Max(0, ActualWidth - 10) && windowPoint.Y <= 10;
        if (inTaskViewCorner && TaskSwitcherOverlay.Visibility != Visibility.Visible)
        {
            if (!_taskViewHotCornerTimer.IsEnabled) _taskViewHotCornerTimer.Start();
        }
        else
        {
            _taskViewHotCornerTimer.Stop();
        }
        var atLeftApplicationEdge = _applicationsSelected && windowPoint.X <= 10 && windowPoint.Y > 20;
        var atRightApplicationEdge = _applicationsSelected && windowPoint.X >= Math.Max(0, ActualWidth - 10) && windowPoint.Y > 20;
        if ((atLeftApplicationEdge || atRightApplicationEdge) && _applicationEdgeReady)
        {
            _applicationEdgeDirection = atRightApplicationEdge ? 1 : -1;
            _applicationEdgeReady = false;
            _applicationEdgeTimer.Start();
        }
        else if (!atLeftApplicationEdge && !atRightApplicationEdge)
        {
            _applicationEdgeTimer.Stop();
            _applicationEdgeReady = true;
        }
        if (_homeHoverLocked)
        {
            var point = e.GetPosition(GamesList);
            if (point.X < 0 || point.Y < 0 || point.X > GamesList.ActualWidth || point.Y > GamesList.ActualHeight)
            {
                _homeHoverLocked = false;
                GamesList.IsHitTestVisible = true;
            }
            return;
        }
        var button = FindAncestor<Button>(e.OriginalSource as DependencyObject);
        if (button is { IsEnabled: true })
        {
            if (!ReferenceEquals(_mouseFocusedButton, button))
            {
                _mouseFocusedButton = button;
                Keyboard.Focus(button);
            }
        }
        else if (_mouseFocusedButton is not null)
        {
            if (ReferenceEquals(Keyboard.FocusedElement, _mouseFocusedButton)) Keyboard.ClearFocus();
            _mouseFocusedButton = null;
        }
    }

    private void Window_PreviewMouseActivity(object sender, MouseEventArgs e)
    {
        ShowCursorForMouseActivity();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => ActivateNonMouseInput();

    private void ActivateNonMouseInput()
    {
        Cursor = Cursors.None;
        _cursorHideTimer.Stop();
        if (_mouseFocusedButton is null) return;
        _mouseSuppressedButton = _mouseFocusedButton;
        _mouseFocusedButton = null;
        _mouseSuppressedButton.IsHitTestVisible = false;
    }

    private void ShowCursorForMouseActivity()
    {
        Cursor = Cursors.Arrow;
        _cursorHideTimer.Stop();
        if (_inputSettings.InputMethod == "Mouse & Keyboard") return;
        _cursorHideTimer.Start();
    }

    private void LoadGames()
    {
        try
        {
            var games = GameLibrary.Load();
            _applications = FilterGameShortcuts(ApplicationScanner.Scan(), games);
            RefreshQuickLaunchItems();
            DisplayGames(games);
        }
        catch (Exception exception)
        {
            EmptyLibrary.Visibility = Visibility.Visible;
            StatusText.Text = exception.Message.ToUpperInvariant();
        }
    }

    private void DisplayGames(IReadOnlyList<GameEntry> games)
    {
        _allGames = games;
        var visibleGames = games.Where(game => !game.IsHidden).ToList();
        GamesList.ItemsSource = visibleGames;
        UpdateContinuePlaying();
        PopulateLibraryFilters();
        SetEmptyLibrary(visibleGames.Count == 0, "NO GAMES FOUND", "Refresh the library to scan again.");
        StatusText.Text = _homeSelected ? "HOME" : visibleGames.Count == 1 ? "1 GAME READY" : $"{visibleGames.Count} GAMES READY";
    }

    private static IReadOnlyList<GameEntry> FilterGameShortcuts(IReadOnlyList<GameEntry> applications, IReadOnlyList<GameEntry> games)
    {
        static string Normalize(string value) => Regex.Replace(value, @"[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase).ToLowerInvariant();
        var gameNames = games.Select(game => Normalize(game.Name)).Where(name => name.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gameTargets = games.Select(game => game.Target).Where(target => !string.IsNullOrWhiteSpace(target)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return applications.Where(app => !gameTargets.Contains(app.Target) && !gameNames.Contains(Normalize(app.Name))).ToList();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("dddd, dd MMMM").ToUpperInvariant();
        UpdateBattery();
    }

    private void UpdateBattery()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryLifePercent == byte.MaxValue)
        {
            BatteryFill.Width = 0;
            ChargingIcon.Visibility = Visibility.Collapsed;
            WifiStatusIcon.Opacity = NetworkInterface.GetIsNetworkAvailable() ? 1 : 0.35;
            return;
        }

        var percentage = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
        BatteryFill.Width = 16d * percentage / 100d;
        BatteryFill.Background = percentage > 50
            ? BatteryGreen
            : percentage > 20 ? BatteryYellow : BatteryRed;
        ChargingIcon.Visibility = status.AcLineStatus == 1 ? Visibility.Visible : Visibility.Collapsed;
        WifiStatusIcon.Opacity = NetworkInterface.GetIsNetworkAvailable() ? 1 : 0.35;
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

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

    private void Game_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameEntry game }) return;

        if (game.IsApplication)
        {
            LaunchApplication(game);
            return;
        }

        _selectedDetailsGame = game;
        GameDetailsPage.DataContext = game;
        HideGameBackground();
        GameDetailsPage.Visibility = Visibility.Visible;
        LaunchDetailsButton.Focus();
    }

    private void ContinueGame_Click(object sender, RoutedEventArgs e)
    {
        _lastHomeSelection = "Continue";
        if (ContinuePlayingPanel.DataContext is GameEntry game) LaunchGame(game);
    }

    private void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameEntry application }) return;
        _lastHomeSelection = application.Name;
        LaunchApplication(application);
    }

    private void RefreshQuickLaunchItems()
    {
        var pins = QuickLaunchUsageStore.LoadPins();
        QuickLaunchList.ItemsSource = _applications
            .Where(app => pins.Contains(app.Target))
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Take(9).ToList();
    }

    private void LaunchApplication(GameEntry application)
    {
        try
        {
            var (launchTarget, shortcutArguments) = ResolveShortcutLaunch(application.Target);
            if (launchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var processName = Path.GetFileNameWithoutExtension(launchTarget);
                var running = Process.GetProcessesByName(processName)
                    .FirstOrDefault(process => process.MainWindowHandle != IntPtr.Zero);
                if (running is not null)
                {
                    ShowWindow(running.MainWindowHandle, 9);
                    SetForegroundWindow(running.MainWindowHandle);
                    StatusText.Text = $"SWITCHED TO {application.Name.ToUpperInvariant()}";
                    return;
                }
            }
            var arguments = string.Join(" ", new[] { shortcutArguments, application.Arguments }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var executableTarget = File.Exists(launchTarget) ? launchTarget : application.Target;
            var startInfo = new ProcessStartInfo(executableTarget)
            {
                UseShellExecute = true,
                Arguments = string.IsNullOrWhiteSpace(arguments) ? string.Empty : arguments,
                WorkingDirectory = launchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(launchTarget) ?? string.Empty : string.Empty
            };
            Process.Start(startInfo);
            StatusText.Text = $"OPENED {application.Name.ToUpperInvariant()}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"FAILED TO OPEN {application.Name.ToUpperInvariant()}";
            ShowNotification($"Could not open {application.Name}: {exception.Message}");
        }
    }

    private void QuickLaunchContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var application = (menu.PlacementTarget as FrameworkElement)?.Tag as GameEntry;
        if (application is null) return;
        var pinned = QuickLaunchUsageStore.LoadPins().Contains(application.Target);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Header is not string header || !header.EndsWith("QUICK LAUNCH")) continue;
            var isUnpin = header.StartsWith("UNPIN");
            item.Visibility = isUnpin == pinned ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PinApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameEntry { IsApplication: true } application }) return;
        QuickLaunchUsageStore.Pin(application.Target);
        RefreshQuickLaunchItems();
        ShowNotification($"{application.Name} pinned to Quick Launch");
    }

    private void UnpinApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameEntry { IsApplication: true } application }) return;
        QuickLaunchUsageStore.Unpin(application.Target);
        RefreshQuickLaunchItems();
        ShowNotification($"{application.Name} removed from Quick Launch");
    }

    private void ApplicationMenuVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameEntry { IsApplication: true } application }) return;
        var visible = !application.IsInApplicationMenu;
        ApplicationMenuStore.SetVisible(application, visible);
        if (string.IsNullOrWhiteSpace(GameSearchBox.Text)) ApplyApplicationFilter();
        else GameSearchBox_TextChanged(GameSearchBox, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
        ShowNotification(visible ? $"{application.Name} added to Applications" : $"{application.Name} removed from Applications");
    }

    private void ApplicationFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filter }) _activeApplicationFilter = filter;
        ApplyApplicationFilter();
    }

    private void ApplyApplicationFilter()
    {
        if (!_applicationsSelected) return;
        UpdateApplicationRunningStates();
        var apps = ApplicationPageSource(_activeApplicationFilter)
            .OrderByDescending(app => app.IsRunning).ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToList();
        SetApplicationResults(apps);
        SetEmptyLibrary(apps.Count == 0, $"NO {_activeApplicationFilter.ToUpperInvariant()} APPLICATIONS", "Choose another application category.");
        StatusText.Text = $"{apps.Count} APPLICATIONS SHOWN";
    }

    private void SetApplicationResults(IReadOnlyList<GameEntry> apps)
    {
        _applicationResults = apps;
        _applicationPage = 0;
        UpdateApplicationPage();
    }

    private void UpdateApplicationPage()
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_applicationResults.Count / (double)ApplicationsPerPage));
        _applicationPage = Math.Clamp(_applicationPage, 0, pageCount - 1);
        ApplicationList.ItemsSource = _applicationResults.Skip(_applicationPage * ApplicationsPerPage)
            .Take(ApplicationsPerPage).ToList();
        ApplicationPageDots.ItemsSource = Enumerable.Range(0, pageCount)
            .Select(index => new ApplicationPageDot(index,
                CreateFrozenBrush(index == _applicationPage ? "#B33A44" : "#55FFFFFF"),
                $"Applications page {index + 1}")).ToList();
    }

    private void PreviousApplicationPage_Click(object sender, RoutedEventArgs e)
    {
        if (_applicationPage <= 0) return;
        _applicationPage--;
        UpdateApplicationPage();
    }

    private void NextApplicationPage_Click(object sender, RoutedEventArgs e)
    {
        if ((_applicationPage + 1) * ApplicationsPerPage >= _applicationResults.Count) return;
        _applicationPage++;
        UpdateApplicationPage();
    }

    private void ApplicationPageDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int page }) return;
        _applicationPage = page;
        UpdateApplicationPage();
    }

    private void ChangeApplicationPage(int direction)
    {
        if (!_applicationsSelected || direction == 0) return;
        var pageCount = Math.Max(1, (int)Math.Ceiling(_applicationResults.Count / (double)ApplicationsPerPage));
        var next = _applicationPage + direction;
        if (next < 0 || next >= pageCount) return;
        _applicationPage = next;
        UpdateApplicationPage();
    }

    private IntPtr WindowMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmMouseHWheel = 0x020E;
        if (message != WmMouseHWheel || !_applicationsSelected) return IntPtr.Zero;
        var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
        ChangeApplicationPage(delta > 0 ? 1 : -1);
        handled = true;
        return IntPtr.Zero;
    }

    private sealed record ApplicationPageDot(int Index, Brush Fill, string AccessibleName);

    private IEnumerable<GameEntry> ApplicationPageSource(string filter, bool includeMenuHidden = false)
    {
        var availableApplications = includeMenuHidden ? _applications : _applications.Where(app => app.IsInApplicationMenu).ToList();
        if (!filter.Equals("Games", StringComparison.OrdinalIgnoreCase))
            return filter == "All" ? availableApplications : availableApplications.Where(app =>
                app.ApplicationCategory.Equals(filter, StringComparison.OrdinalIgnoreCase));

        var launchers = availableApplications.Where(app => app.ApplicationCategory.Equals("Games", StringComparison.OrdinalIgnoreCase));
        var games = _allGames.Where(game => !game.IsHidden).Select(game => new GameEntry
        {
            Name = game.Name, Target = game.Target, Arguments = game.Arguments,
            WorkingDirectory = game.WorkingDirectory, Source = game.Source, IsApplication = true,
            ApplicationCategory = "Games", ApplicationIconPath = ResolveGameApplicationIcon(game)
        });
        return launchers.Concat(games).GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
    }

    private static string ResolveGameApplicationIcon(GameEntry game)
    {
        if (File.Exists(game.Target)) return game.Target;
        if (!string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory))
        {
            try
            {
                var normalizedName = Regex.Replace(game.Name, @"[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);
                var executable = Directory.EnumerateFiles(game.WorkingDirectory, "*.exe", SearchOption.AllDirectories)
                    .Take(400)
                    .Where(path => !Regex.IsMatch(Path.GetFileName(path), "unins|crash|report|setup|redist|launcher|helper", RegexOptions.IgnoreCase))
                    .OrderByDescending(path => Regex.Replace(Path.GetFileNameWithoutExtension(path), @"[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase)
                        .Contains(normalizedName, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(path => new FileInfo(path).Length)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(executable)) return executable;
            }
            catch { }
        }
        return game.Cover ?? game.Target;
    }

    private void UpdateApplicationRunningStates()
    {
        var runningNames = Process.GetProcesses().Select(process => process.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _applications)
        {
            var (target, _) = ResolveShortcutLaunch(app.Target);
            app.IsRunning = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            runningNames.Contains(Path.GetFileNameWithoutExtension(target));
        }
    }

    private static readonly object ShortcutResolveSync = new();
    private static readonly Dictionary<string, (string Target, string? Arguments)> ShortcutResolveCache = new(StringComparer.OrdinalIgnoreCase);

    private static (string Target, string? Arguments) ResolveShortcutLaunch(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return (path, null);
        lock (ShortcutResolveSync)
        {
            if (ShortcutResolveCache.TryGetValue(path, out var cached)) return cached;
        }
        var result = ResolveShortcutTargetCom(path);
        if (!File.Exists(result.Target))
        {
            var powerShell = ResolveShortcutTargetPowerShell(path);
            if (File.Exists(powerShell.Target)) result = powerShell;
        }
        if (!File.Exists(result.Target))
        {
            var swapped = SwapProgramFilesRoot(result.Target);
            if (!string.IsNullOrWhiteSpace(swapped) && File.Exists(swapped)) result = (swapped, result.Arguments);
        }
        lock (ShortcutResolveSync) ShortcutResolveCache[path] = result;
        return result;
    }

    private static string SwapProgramFilesRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path.Contains("\\Program Files (x86)\\", StringComparison.OrdinalIgnoreCase))
            return path.Replace("\\Program Files (x86)\\", "\\Program Files\\", StringComparison.OrdinalIgnoreCase);
        if (path.Contains("\\Program Files\\", StringComparison.OrdinalIgnoreCase))
            return path.Replace("\\Program Files\\", "\\Program Files (x86)\\", StringComparison.OrdinalIgnoreCase);
        return string.Empty;
    }

    private static (string Target, string? Arguments) ResolveShortcutTargetPowerShell(string path)
    {
        try
        {
            var escaped = path.Replace("'", "''");
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = $"-NoProfile -NonInteractive -Command \"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{escaped}'); [pscustomobject]@{{ T = $s.TargetPath; A = $s.Arguments }} | ConvertTo-Json -Compress\""
            });
            if (process is null) return (path, null);
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var target = root.TryGetProperty("T", out var targetValue) ? targetValue.GetString() : null;
            var arguments = root.TryGetProperty("A", out var argsValue) ? argsValue.GetString() : null;
            target = Environment.ExpandEnvironmentVariables((target ?? string.Empty).Trim().Trim('"'));
            return string.IsNullOrWhiteSpace(target) ? (path, null) : (target, arguments);
        }
        catch { return (path, null); }
    }

    private static (string Target, string? Arguments) ResolveShortcutTargetCom(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return (path, null);
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { path });
            if (shortcut is null) return (path, null);
            var shortcutType = shortcut.GetType();
            var target = shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            var arguments = shortcutType.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null) as string;
            target = Environment.ExpandEnvironmentVariables((target ?? string.Empty).Trim().Trim('"'));
            return (target, arguments);
        }
        catch { return (path, null); }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private void PopulateLibraryFilters()
    {
        var selectedStore = StoreFilter.SelectedItem as string;
        StoreFilter.ItemsSource = new[] { "ALL STORES" }.Concat(_allGames.Select(game => game.StoreName).Distinct(StringComparer.OrdinalIgnoreCase).Order()).ToList();
        StoreFilter.SelectedItem = StoreFilter.Items.Contains(selectedStore) ? selectedStore : "ALL STORES";
    }

    private void LibraryFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filter }) _activeLibraryFilter = filter;
        ApplyLibraryFilters();
    }

    private void LibraryFilterPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyLibraryFilters();
    }

    private void ApplyLibraryFilters()
    {
        if (_homeSelected || _applicationsSelected) return;
        IEnumerable<GameEntry> games = _allGames;
        games = _activeLibraryFilter switch
        {
            "Favorites" => games.Where(game => game.IsFavorite && !game.IsHidden),
            "Recent" => games.Where(game => game.LastPlayedUtc.HasValue && !game.IsHidden).OrderByDescending(game => game.LastPlayedUtc),
            "Hidden" => games.Where(game => game.IsHidden),
            _ => games.Where(game => !game.IsHidden)
        };
        if (StoreFilter.SelectedItem is string store && store != "ALL STORES")
            games = games.Where(game => game.StoreName.Equals(store, StringComparison.OrdinalIgnoreCase));
        var result = games.ToList();
        GamesList.ItemsSource = result;
        var (emptyTitle, emptyMessage) = EmptyFilterMessage();
        SetEmptyLibrary(result.Count == 0, emptyTitle, emptyMessage);
        StatusText.Text = $"{result.Count} GAMES SHOWN";
    }

    private (string Title, string Message) EmptyFilterMessage()
    {
        if (StoreFilter.SelectedItem is string store && store != "ALL STORES")
            return ($"NO {store.ToUpperInvariant()} GAMES", "Choose another store or select All.");
        return _activeLibraryFilter switch
        {
            "Favorites" => ("NO FAVOURITES YET", "Mark a game as a favourite to see it here."),
            "Recent" => ("NO GAMES PLAYED RECENTLY", "Games you launch will appear here."),
            "Hidden" => ("NO HIDDEN GAMES", "Games you hide will appear here."),
            _ => ("NO GAMES FOUND", "Refresh the library to scan again.")
        };
    }

    private void SetEmptyLibrary(bool visible, string title, string message)
    {
        EmptyLibraryTitle.Text = title;
        EmptyLibraryMessage.Text = message;
        EmptyLibrary.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowNotification(string message)
    {
        NotificationText.Text = message.ToUpperInvariant();
        NotificationToast.Visibility = Visibility.Visible;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void UpdateContinuePlaying()
    {
        var game = _allGames
            .Where(entry => !entry.IsApplication && !entry.IsHidden && entry.LastPlayedUtc.HasValue)
            .OrderByDescending(entry => entry.LastPlayedUtc)
            .FirstOrDefault();

        ContinuePlayingPanel.DataContext = game;
        ContinuePlayingPanel.Visibility = _homeSelected && game is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (game is null)
        {
            ContinueCoverBrush.ImageSource = null;
            return;
        }

        ContinueGameName.Text = game.Name.ToUpperInvariant();
        ContinuePlayTime.Text = $"{FormatPlayTime(TimeSpan.FromSeconds(game.TotalPlayTimeSeconds))} PLAYED";
        ContinueLastPlayed.Text = FormatLastPlayed(game.LastPlayedUtc!.Value);
        ContinueCoverBrush.ImageSource = LoadLocalImage(game.Cover);
    }

    private static string FormatLastPlayed(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var days = (DateTime.Today - local.Date).Days;
        return days switch
        {
            0 => "PLAYED TODAY",
            1 => "PLAYED YESTERDAY",
            _ when days > 1 && days < 7 => $"PLAYED {days} DAYS AGO",
            _ => $"LAST PLAYED {local:dd MMM yyyy}".ToUpperInvariant()
        };
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

    private void DetailsBack_Click(object sender, RoutedEventArgs e) => CloseGameDetails();

    private void CloseGameDetails()
    {
        GameDetailsPage.Visibility = Visibility.Collapsed;
        GameDetailsPage.DataContext = null;
        _selectedDetailsGame = null;
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void LaunchDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetailsGame is not null) LaunchGame(_selectedDetailsGame);
    }

    private void GameFavoriteContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameEntry game }) return;
        game.IsFavorite = !game.IsFavorite;
        LibraryPreferencesStore.SetFavorite(game, game.IsFavorite);
        _allGames = _allGames.OrderByDescending(item => item.IsFavorite)
            .ThenByDescending(item => item.LastPlayedUtc).ThenBy(item => item.Name).ToList();
        ApplyLibraryFilters();
        GamesList.Items.Refresh();
        ShowNotification(game.IsFavorite ? $"{game.Name} added to Favorites" : $"{game.Name} removed from Favorites");
    }

    private void GameHiddenContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: GameEntry game }) return;
        game.IsHidden = !game.IsHidden;
        LibraryPreferencesStore.SetHidden(game, game.IsHidden);
        ApplyLibraryFilters();
        ShowNotification(game.IsHidden ? $"{game.Name} hidden" : $"{game.Name} restored");
    }

    private void HandleControllerAcceptHeld()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement is not DependencyObject focused) return;
            var button = FindAncestor<Button>(focused);
            if (button?.Tag is not GameEntry { IsApplication: false } || button.ContextMenu is null) return;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Center;
            button.ContextMenu.IsOpen = true;
        });
    }

    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetailsGame is null) return;
        var folder = ResolveWorkingDirectory(_selectedDetailsGame);
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void HideGame_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetailsGame is null) return;
        var game = _selectedDetailsGame;
        game.IsHidden = !game.IsHidden;
        LibraryPreferencesStore.SetHidden(game, game.IsHidden);
        CloseGameDetails();
        ApplyLibraryFilters();
        StatusText.Text = game.IsHidden ? "GAME HIDDEN FROM LIBRARY" : "GAME RESTORED TO LIBRARY";
    }

    private void EditMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetailsGame is null) return;
        EditGameName.Text = _selectedDetailsGame.Name; EditGenres.Text = _selectedDetailsGame.Genres;
        EditDeveloper.Text = _selectedDetailsGame.Developer; EditPublisher.Text = _selectedDetailsGame.Publisher;
        EditReleaseDate.Text = _selectedDetailsGame.ReleaseDate; EditPlatforms.Text = _selectedDetailsGame.Platforms;
        EditDescription.Text = _selectedDetailsGame.Description;
        _pendingCorrectedCover = _selectedDetailsGame.Cover;
        _pendingCorrectedBackground = _selectedDetailsGame.Background;
        MetadataCorrectionOverlay.Visibility = Visibility.Visible;
        EditGameName.Focus();
    }

    private void ChangeCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dialog.ShowDialog(this) == true)
            _pendingCorrectedCover = ArtworkCache.NormalizeCover(dialog.FileName,
                $"manual-cover-{_selectedDetailsGame?.Target}-{DateTime.UtcNow.Ticks}");
    }

    private void ChangeBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dialog.ShowDialog(this) == true)
            _pendingCorrectedBackground = ArtworkCache.NormalizeBackground(dialog.FileName,
                $"manual-background-{_selectedDetailsGame?.Target}-{DateTime.UtcNow.Ticks}");
    }

    private void SaveMetadataCorrection_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetailsGame is null) return;
        _selectedDetailsGame.Name = string.IsNullOrWhiteSpace(EditGameName.Text) ? _selectedDetailsGame.Name : EditGameName.Text.Trim();
        _selectedDetailsGame.Genres = EditGenres.Text.Trim(); _selectedDetailsGame.Developer = EditDeveloper.Text.Trim();
        _selectedDetailsGame.Publisher = EditPublisher.Text.Trim(); _selectedDetailsGame.ReleaseDate = EditReleaseDate.Text.Trim();
        _selectedDetailsGame.Platforms = EditPlatforms.Text.Trim(); _selectedDetailsGame.Description = EditDescription.Text.Trim();
        _selectedDetailsGame.Cover = _pendingCorrectedCover; _selectedDetailsGame.Background = _pendingCorrectedBackground;
        MetadataOverrideStore.Save(_selectedDetailsGame);
        MetadataCorrectionOverlay.Visibility = Visibility.Collapsed;
        GameDetailsPage.DataContext = null; GameDetailsPage.DataContext = _selectedDetailsGame;
        GamesList.Items.Refresh();
        StatusText.Text = "GAME METADATA SAVED";
        LaunchDetailsButton.Focus();
    }

    private void CancelMetadataCorrection_Click(object sender, RoutedEventArgs e)
    {
        MetadataCorrectionOverlay.Visibility = Visibility.Collapsed;
        LaunchDetailsButton.Focus();
    }

    private async void LaunchGame(GameEntry game)
    {
        try
        {
            if (_gameSession.IsActive)
            {
                StatusText.Text = ReferenceEquals(_gameSession.CurrentGame, game) ||
                                  _gameSession.CurrentGame?.Target == game.Target
                    ? $"{game.Name.ToUpperInvariant()} IS ALREADY RUNNING"
                    : $"{_gameSession.CurrentGame?.Name.ToUpperInvariant()} IS CURRENTLY RUNNING";
                _gameSession.FocusGame();
                return;
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = game.Target,
                Arguments = game.Arguments,
                UseShellExecute = true,
                WorkingDirectory = ResolveWorkingDirectory(game)
            };
            await _gameSession.LaunchAsync(game, startInfo, new WindowInteropHelper(this).Handle);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"FAILED TO LAUNCH {game.Name.ToUpperInvariant()}: {exception.Message}";
        }
    }

    private void HandleGameSessionStateChanged(GameEntry game, GameSessionState state, TimeSpan duration)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = state switch
            {
                GameSessionState.Launching => $"LAUNCHING {game.Name.ToUpperInvariant()}...",
                GameSessionState.Running => $"{game.Name.ToUpperInvariant()} IS RUNNING",
                GameSessionState.Closed => $"{game.Name.ToUpperInvariant()} CLOSED  •  PLAYED {FormatPlayTime(duration)}",
                _ => $"FAILED TO DETECT {game.Name.ToUpperInvariant()}"
            };
            if (state == GameSessionState.Failed) ShowNotification($"Game launch failed: {game.Name}");
            if (state == GameSessionState.Closed)
            {
                Show();
                WindowState = WindowState.Maximized;
                Activate();
                Topmost = true;
                Dispatcher.BeginInvoke(() => Topmost = false, DispatcherPriority.ApplicationIdle);
                MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        });
    }

    private static string FormatPlayTime(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}H {duration.Minutes}M"
        : $"{Math.Max(1, duration.Minutes)}M";

    private void Game_MouseEnter(object sender, MouseEventArgs e)
    {
        _coverExitTimer.Stop();
        ActivateGameLibraryFromHome();
        QuickLaunchHost.Visibility = Visibility.Collapsed;
        PerformanceModeHost.Visibility = Visibility.Collapsed;
        ShowGameBackground(sender);
    }

    private void Game_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button { IsKeyboardFocusWithin: true }) Keyboard.ClearFocus();
        _coverExitTimer.Stop();
        _coverExitTimer.Start();
    }

    private void Game_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _coverExitTimer.Stop();
        ActivateGameLibraryFromHome();
        QuickLaunchHost.Visibility = Visibility.Collapsed;
        PerformanceModeHost.Visibility = Visibility.Collapsed;
        ShowGameBackground(sender);
    }

    private void ActivateGameLibraryFromHome()
    {
        if (!_homeSelected) return;
        _homeSelected = false;
        _homeHoverLocked = false;
        GamesList.IsHitTestVisible = true;
        ContinuePlayingPanel.Visibility = Visibility.Collapsed;
        QuickLaunchHost.Visibility = Visibility.Visible;
        PerformanceModeHost.Visibility = Visibility.Visible;
        LibraryFilters.Visibility = Visibility.Visible;
        _applicationsSelected = false;
        GameSearchBox.Text = string.Empty;
        GameSearchHost.Visibility = Visibility.Visible;
        GameSearchHost.Margin = new Thickness(0, 0, 0, 349);
        GameLibraryHeader.Foreground = Brushes.White;
        ApplicationsHeader.Foreground = CreateFrozenBrush("#8A8A8A");
        StatusText.Text = $"{_allGames.Count} GAMES READY";
    }

    private void Game_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button { IsMouseOver: true })
        {
            _coverExitTimer.Stop();
            _coverExitTimer.Start();
        }
    }

    private void ShowGameBackground(object sender)
    {
        if (sender is not Button { Tag: GameEntry game })
        {
            HideGameBackground();
            return;
        }
        ShowGameBackground(game);
    }

    private void ShowGameBackground(GameEntry game)
    {
        var image = LoadLocalImage(game.Background);
        if (image is not null)
        {
            FallbackGameBackground.Source = image;
            FallbackGameBackground.Opacity = 0.52;
        }
        else
        {
            HideGameBackground();
            return;
        }
        GameBackgroundShade.Opacity = 1;
    }

    private void HideGameBackground()
    {
        FallbackGameBackground.Opacity = 0;
        FallbackGameBackground.Source = null;
        GameBackgroundShade.Opacity = 0;
    }

    private static string ResolveWorkingDirectory(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.WorkingDirectory)) return game.WorkingDirectory;
        if (Uri.TryCreate(game.Target, UriKind.Absolute, out var uri) && !uri.IsFile) return AppContext.BaseDirectory;
        return Path.GetDirectoryName(game.Target) ?? AppContext.BaseDirectory;
    }

    private void Restart_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false });

    private void PowerOptions_Click(object sender, RoutedEventArgs e)
    {
        PowerButton.Tag = "OverlayOpen";
        PowerOverlay.Visibility = Visibility.Visible;
        PowerOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void PowerOverlayBack_Click(object sender, RoutedEventArgs e) => ClosePowerOptions();

    private void SettingsOverlayBack_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsHomePanel.Visibility == Visibility.Visible) { CloseSettings(); return; }
        if (ControllerCalibrationPanel.Visibility == Visibility.Visible)
        {
            ControllerCalibrationPanel.Visibility = Visibility.Collapsed;
            ControllerSettingsPanel.Visibility = Visibility.Visible;
            ControllerSettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }
        if (AddMetadataSourcePanel.Visibility == Visibility.Visible || RemoveSourceListPanel.Visibility == Visibility.Visible ||
            RemoveMetadataSourcePanel.Visibility == Visibility.Visible)
        {
            AddMetadataSourcePanel.Visibility = Visibility.Collapsed;
            RemoveSourceListPanel.Visibility = Visibility.Collapsed;
            RemoveMetadataSourcePanel.Visibility = Visibility.Collapsed;
            MetadataSettingsPanel.Visibility = Visibility.Visible;
            MetadataSettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }
        BackToSettingsHome_Click(sender, e);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsButton.Tag = "OverlayOpen";
        SettingsHomePanel.Visibility = Visibility.Visible;
        ControllerSettingsPanel.Visibility = Visibility.Collapsed;
        MetadataSettingsPanel.Visibility = Visibility.Collapsed;
        NetworkSettingsPanel.Visibility = Visibility.Collapsed;
        AddMetadataSourcePanel.Visibility = Visibility.Collapsed;
        RemoveSourceListPanel.Visibility = Visibility.Collapsed;
        RemoveMetadataSourcePanel.Visibility = Visibility.Collapsed;
        ControllerCalibrationPanel.Visibility = Visibility.Collapsed;
        MetadataSettingsHomeButton.Visibility = Visibility.Visible;
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void ShowControllerSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsHomePanel.Visibility = Visibility.Collapsed;
        ControllerSettingsPanel.Visibility = Visibility.Visible;
        ControllerSettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void ShowMetadataSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsHomePanel.Visibility = Visibility.Collapsed;
        MetadataSettingsPanel.Visibility = Visibility.Visible;
        MetadataSettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void ShowNetworkSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsHomePanel.Visibility = Visibility.Collapsed;
        NetworkSettingsPanel.Visibility = Visibility.Visible;
        UpdateNetworkSettingsStatus();
        NetworkSettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void RefreshNetworkStatus_Click(object sender, RoutedEventArgs e) => UpdateNetworkSettingsStatus();

    private async void WifiStatusIcon_Click(object sender, MouseButtonEventArgs e) => await OpenConnectionsAsync(true);
    private async void BluetoothStatusIcon_Click(object sender, MouseButtonEventArgs e) => await OpenConnectionsAsync(false);
    private async void OpenAvailableNetworks_Click(object sender, RoutedEventArgs e) => await OpenConnectionsAsync(true);
    private async void OpenBluetoothDevices_Click(object sender, RoutedEventArgs e) => await OpenConnectionsAsync(false);

    private async Task OpenConnectionsAsync(bool wifi)
    {
        WifiPasswordPanel.Visibility = Visibility.Collapsed;
        WifiPasswordBox.Clear();
        _pendingWifiNetwork = null;
        WifiConnectionsPanel.Visibility = wifi ? Visibility.Visible : Visibility.Collapsed;
        BluetoothConnectionsPanel.Visibility = wifi ? Visibility.Collapsed : Visibility.Visible;
        if (!wifi && _cachedBluetoothDevices.Count > 0) BluetoothDevicesList.ItemsSource = _cachedBluetoothDevices;
        ConnectionsOverlay.Visibility = Visibility.Visible;
        await RefreshConnectionsAsync(wifi);
        if (wifi) _wifiScanTimer.Start();
        else _wifiScanTimer.Stop();
        ConnectionsOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private async Task RefreshConnectionsAsync(bool wifi, bool showStatus = true)
    {
        if (wifi && _wifiScanInProgress) return;
        if (wifi) _wifiScanInProgress = true;
        if (showStatus) StatusText.Text = wifi ? "SCANNING WI-FI NETWORKS" : "SCANNING BLUETOOTH DEVICES";
        try
        {
            if (wifi) WifiNetworksList.ItemsSource = await ConnectionService.ScanWifiAsync();
            else
            {
                _cachedBluetoothDevices = await Task.Run(ConnectionService.ScanBluetooth);
                BluetoothDevicesList.ItemsSource = _cachedBluetoothDevices;
            }
            if (showStatus) StatusText.Text = "CONNECTION SCAN COMPLETE";
        }
        catch (Exception exception) { ShowNotification($"Connection scan failed: {exception.Message}"); }
        finally { if (wifi) _wifiScanInProgress = false; }
    }

    private async Task PreloadBluetoothDevicesAsync()
    {
        try
        {
            _cachedBluetoothDevices = await Task.Run(ConnectionService.ScanBluetooth);
            BluetoothDevicesList.ItemsSource = _cachedBluetoothDevices;
        }
        catch { _cachedBluetoothDevices = Array.Empty<BluetoothDevice>(); }
    }

    private async void RefreshConnections_Click(object sender, RoutedEventArgs e) =>
        await RefreshConnectionsAsync(WifiConnectionsPanel.Visibility == Visibility.Visible);
    private async void ConnectWifi_Click(object sender, RoutedEventArgs e)
    {
        if (WifiNetworksList.SelectedItem is not WifiNetwork network) return;
        try { await ConnectionService.ConnectWifiAsync(network, WifiPasswordBox.Password); ShowNotification($"Connected to {network.Name}"); }
        catch (Exception exception) { ShowNotification($"Wi-Fi connection failed: {exception.Message}"); }
    }
    private async void WifiNetworkAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WifiNetwork network }) return;
        try
        {
            if (network.IsConnected)
            {
                await ConnectionService.DisconnectWifiAsync();
                ShowNotification($"Disconnected from {network.Name}");
            }
            else
            {
                if (network.Secured)
                {
                    _pendingWifiNetwork = network;
                    WifiNetworksList.SelectedItem = network;
                    WifiPasswordPanel.Visibility = Visibility.Visible;
                    WifiPasswordBox.Focus();
                    return;
                }
                await ConnectionService.ConnectWifiAsync(network, null);
                ShowNotification($"Connected to {network.Name}");
            }
            await RefreshConnectionsAsync(true);
        }
        catch (Exception exception) { ShowNotification($"Wi-Fi action failed: {exception.Message}"); }
    }

    private async void ConfirmWifiPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingWifiNetwork is not { } network) return;
        if (string.IsNullOrWhiteSpace(WifiPasswordBox.Password))
        {
            ShowNotification("Enter the Wi-Fi password");
            WifiPasswordBox.Focus();
            return;
        }
        try
        {
            await ConnectionService.ConnectWifiAsync(network, WifiPasswordBox.Password);
            ShowNotification($"Connected to {network.Name}");
            WifiPasswordBox.Clear();
            WifiPasswordPanel.Visibility = Visibility.Collapsed;
            _pendingWifiNetwork = null;
            await RefreshConnectionsAsync(true);
        }
        catch (Exception exception) { ShowNotification($"Wi-Fi connection failed: {exception.Message}"); }
    }
    private async void DisconnectWifi_Click(object sender, RoutedEventArgs e)
    {
        try { await ConnectionService.DisconnectWifiAsync(); ShowNotification("Wi-Fi disconnected"); }
        catch (Exception exception) { ShowNotification($"Wi-Fi disconnect failed: {exception.Message}"); }
    }
    private async void PairBluetooth_Click(object sender, RoutedEventArgs e)
    {
        if (BluetoothDevicesList.SelectedItem is not BluetoothDevice device) return;
        var paired = ConnectionService.PairBluetooth(new WindowInteropHelper(this).Handle, device);
        ShowNotification(paired ? $"{device.Name} paired" : $"Could not pair {device.Name}");
        await RefreshConnectionsAsync(false);
    }
    private async void RemoveBluetooth_Click(object sender, RoutedEventArgs e)
    {
        if (BluetoothDevicesList.SelectedItem is not BluetoothDevice device) return;
        var removed = await Task.Run(() => ConnectionService.RemoveBluetooth(device));
        ShowNotification(removed ? $"{device.Name} removed" : $"Could not remove {device.Name}");
        await RefreshConnectionsAsync(false);
    }
    private async void BluetoothDeviceAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BluetoothDevice device }) return;
        bool success;
        if (device.Connected) success = await Task.Run(() => ConnectionService.RemoveBluetooth(device));
        else success = ConnectionService.PairBluetooth(new WindowInteropHelper(this).Handle, device);
        ShowNotification(success
            ? device.Connected ? $"{device.Name} disconnected" : $"{device.Name} connected"
            : $"Could not {(device.Connected ? "disconnect" : "connect")} {device.Name}");
        await RefreshConnectionsAsync(false);
    }
    private void CloseConnections_Click(object sender, RoutedEventArgs e)
    {
        _wifiScanTimer.Stop();
        ConnectionsOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateNetworkSettingsStatus()
    {
        var connection = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .OrderByDescending(adapter => adapter.Speed)
            .FirstOrDefault(adapter => adapter.GetIPProperties().UnicastAddresses.Any(address =>
                address.Address.AddressFamily == AddressFamily.InterNetwork));
        if (connection is null)
        {
            NetworkConnectionText.Text = "OFFLINE";
            NetworkConnectionText.Foreground = CreateFrozenBrush("#A0A0A0");
            NetworkAddressText.Text = "NO ACTIVE WIRELESS CONNECTION";
            WifiStatusIcon.Opacity = 0.35;
        }
        else
        {
            var address = connection.GetIPProperties().UnicastAddresses
                .FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            NetworkConnectionText.Text = connection.Name.ToUpperInvariant();
            NetworkConnectionText.Foreground = Brushes.White;
            NetworkAddressText.Text = "CONNECTED" + (address is null ? string.Empty : $"  •  {address}");
            WifiStatusIcon.Opacity = 1;
        }

        var bluetoothAvailable = IsBluetoothRadioAvailable();
        BluetoothConnectionText.Text = bluetoothAvailable ? "AVAILABLE" : "NOT DETECTED";
        BluetoothConnectionText.Foreground = bluetoothAvailable ? Brushes.White : CreateFrozenBrush("#A0A0A0");
        BluetoothDetailsText.Text = bluetoothAvailable
            ? "BLUETOOTH RADIO DETECTED"
            : "NO BLUETOOTH RADIO IS AVAILABLE";
        BluetoothStatusIcon.Opacity = bluetoothAvailable ? 1 : 0.35;
    }

    private static bool IsBluetoothRadioAvailable()
    {
        var parameters = new BluetoothFindRadioParams { Size = Marshal.SizeOf<BluetoothFindRadioParams>() };
        var findHandle = BluetoothFindFirstRadio(ref parameters, out var radioHandle);
        if (findHandle == IntPtr.Zero) return false;
        if (radioHandle != IntPtr.Zero) CloseHandle(radioHandle);
        BluetoothFindRadioClose(findHandle);
        return true;
    }

    private void OpenWindowsSetting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string uri }) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception exception) { ShowNotification($"Could not open network settings: {exception.Message}"); }
    }

    private void OpenNetworkAdapters_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "ncpa.cpl") { UseShellExecute = true }); }
        catch (Exception exception) { ShowNotification($"Could not open network adapters: {exception.Message}"); }
    }

    private void BackToSettingsHome_Click(object sender, RoutedEventArgs e)
    {
        ControllerSettingsPanel.Visibility = Visibility.Collapsed;
        MetadataSettingsPanel.Visibility = Visibility.Collapsed;
        NetworkSettingsPanel.Visibility = Visibility.Collapsed;
        SettingsHomePanel.Visibility = Visibility.Visible;
        SettingsHomePanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void LoadInputSettings()
    {
        _inputSettings = ControllerSettingsStore.Load();
        InputMethodPicker.ItemsSource = new[] { "Controller", "Mouse & Keyboard" };
        InputMethodPicker.SelectedItem = _inputSettings.InputMethod;
        GuideHomeToggle.IsChecked = _inputSettings.GuideOpensHomeWhileGaming;
        NavigationAudioToggle.IsChecked = _inputSettings.NavigationAudioEnabled;
        NavigationAudioVolumeText.Text = $"{_inputSettings.NavigationAudioVolume}%";
        if (_inputSettings.PerformanceMode is "Quiet" or "Power Saver") _inputSettings.PerformanceMode = "Eco";
        if (_inputSettings.PerformanceMode == "Performance") _inputSettings.PerformanceMode = "Ultimate";
        if (_inputSettings.PerformanceMode is not ("Ultimate" or "Balanced" or "Eco"))
            _inputSettings.PerformanceMode = "Balanced";
        UpdatePerformanceModeButtons();
        ApplyInputMethod();
        ApplyPerformanceMode(false);
    }

    private void PerformanceMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        _inputSettings.PerformanceMode = mode;
        UpdatePerformanceModeButtons();
        ControllerSettingsStore.Save(_inputSettings);
        ApplyPerformanceMode(true);
    }

    private void UpdatePerformanceModeButtons()
    {
        UltimateModeButton.Background = CreateFrozenBrush(_inputSettings.PerformanceMode == "Ultimate" ? "#C90030" : "#18FFFFFF");
        BalancedModeButton.Background = CreateFrozenBrush(_inputSettings.PerformanceMode == "Balanced" ? "#246BCE" : "#18FFFFFF");
        EcoModeButton.Background = CreateFrozenBrush(_inputSettings.PerformanceMode == "Eco" ? "#188A4F" : "#18FFFFFF");
    }

    private void ApplyPerformanceMode(bool reportStatus)
    {
        var scheme = _inputSettings.PerformanceMode switch
        {
            "Ultimate" => "e9a42b02-d5df-448d-aa00-03f14749eb61",
            "Eco" => "a1841308-3541-4fab-bc81-f71556f20b4a",
            _ => "381b4222-f694-41f0-9685-ff5bb260df2e"
        };
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powercfg.exe", $"/setactive {scheme}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null || !process.WaitForExit(3000) || process.ExitCode != 0)
                throw new InvalidOperationException("Windows rejected the requested power plan.");
            if (reportStatus) StatusText.Text = $"{_inputSettings.PerformanceMode.ToUpperInvariant()} MODE ACTIVE";
        }
        catch (Exception exception)
        {
            if (reportStatus) ShowNotification($"Could not change performance mode: {exception.Message}");
        }
    }

    private void InputMethodPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InputMethodPicker.SelectedItem is not string method) return;
        _inputSettings.InputMethod = method;
        ControllerSettingsStore.Save(_inputSettings);
        ApplyInputMethod();
        StatusText.Text = $"INPUT METHOD: {method.ToUpperInvariant()}";
    }

    private void ApplyInputMethod()
    {
        _controller.IsEnabled = _inputSettings.InputMethod == "Controller";
        if (_controller.IsEnabled) Cursor = Cursors.None;
        else { _cursorHideTimer.Stop(); Cursor = Cursors.Arrow; }
    }

    private void GuideHomeToggle_Click(object sender, RoutedEventArgs e)
    {
        _inputSettings.GuideOpensHomeWhileGaming = GuideHomeToggle.IsChecked == true;
        ControllerSettingsStore.Save(_inputSettings);
        StatusText.Text = _inputSettings.GuideOpensHomeWhileGaming
            ? "GUIDE OPENS OMEN HOME WHILE GAMING"
            : "GUIDE HOME OVERLAY DISABLED";
    }

    private void NavigationAudioToggle_Click(object sender, RoutedEventArgs e)
    {
        _inputSettings.NavigationAudioEnabled = NavigationAudioToggle.IsChecked == true;
        ControllerSettingsStore.Save(_inputSettings);
        if (_inputSettings.NavigationAudioEnabled)
            NavigationSound.Play(ControllerCommand.Accept, _inputSettings.NavigationAudioVolume);
    }

    private void DecreaseAudioVolume_Click(object sender, RoutedEventArgs e) => ChangeAudioVolume(-5);
    private void IncreaseAudioVolume_Click(object sender, RoutedEventArgs e) => ChangeAudioVolume(5);

    private void ChangeAudioVolume(int amount)
    {
        _inputSettings.NavigationAudioVolume = Math.Clamp(_inputSettings.NavigationAudioVolume + amount, 0, 100);
        NavigationAudioVolumeText.Text = $"{_inputSettings.NavigationAudioVolume}%";
        ControllerSettingsStore.Save(_inputSettings);
        if (_inputSettings.NavigationAudioEnabled)
            NavigationSound.Play(ControllerCommand.Accept, _inputSettings.NavigationAudioVolume);
    }

    private void HandleGuideButton()
    {
        var shellHandle = new WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        if (foreground == shellHandle) ShowHomePage();
        else OpenContextualGameLibrary();
    }

    private void OpenContextualGameLibrary()
    {
        var shellHandle = new WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        if (foreground == shellHandle)
        {
            ShowHomePage();
            return;
        }

        Show();
        WindowState = WindowState.Maximized;
        Activate();
        Topmost = true;
        Dispatcher.BeginInvoke(() => Topmost = false, DispatcherPriority.ApplicationIdle);
        ShowHomePage();
        var windows = GetTaskWindows(shellHandle).Take(6).ToList();
        HomeTaskWindowList.ItemsSource = windows;
        HomeTaskWindowsHost.Visibility = windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            RegisterHomeTaskThumbnails();
        }, DispatcherPriority.Loaded);
    }

    private void HandleWindowsKey()
    {
        Show();
        WindowState = WindowState.Maximized;
        Activate();
        Topmost = true;
        Dispatcher.BeginInvoke(() => Topmost = false, DispatcherPriority.ApplicationIdle);
        ShowHomePage();
    }

    private void OpenSearchFromHome()
    {
        if (GameDetailsPage.Visibility == Visibility.Visible ||
            PowerOverlay.Visibility == Visibility.Visible ||
            SettingsOverlay.Visibility == Visibility.Visible) return;
        if (_homeSelected)
        {
            return;
        }
        GameSearchHost.Visibility = Visibility.Visible;
        GameSearchHost.Margin = new Thickness(0, 0, 0, 349);
        GameSearchBox.Focus();
        GameSearchBox.SelectAll();
    }

    private void GameSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var query = GameSearchBox.Text.Trim();
        var source = _applicationsSelected
            ? ApplicationPageSource(string.IsNullOrWhiteSpace(query) ? _activeApplicationFilter : "All",
                !string.IsNullOrWhiteSpace(query)).ToList()
            : _allGames.Where(game => !game.IsHidden).ToList();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? source
            : source.Where(game => game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   (game.Genres?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                   (game.Developer?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                   game.StoreName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (_applicationsSelected) SetApplicationResults(filtered);
        else GamesList.ItemsSource = filtered;
        SetEmptyLibrary(filtered.Count == 0, _applicationsSelected ? "NO MATCHING APPLICATIONS" : "NO MATCHING GAMES", "Try a different search term.");
        StatusText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{source.Count} {(_applicationsSelected ? "APPLICATIONS" : "GAMES")} READY"
            : $"{filtered.Count} SEARCH RESULT{(filtered.Count == 1 ? string.Empty : "S")}";
    }

    private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || GameSearchBox.IsKeyboardFocusWithin) return;
        if (PowerOverlay.Visibility == Visibility.Visible || SettingsOverlay.Visibility == Visibility.Visible ||
            ConnectionsOverlay.Visibility == Visibility.Visible || TaskSwitcherOverlay.Visibility == Visibility.Visible ||
            ErrorLogOverlay.Visibility == Visibility.Visible ||
            GameDetailsPage.Visibility == Visibility.Visible || MetadataCorrectionOverlay.Visibility == Visibility.Visible) return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox) return;

        GameSearchHost.Visibility = Visibility.Visible;
        GameSearchBox.Focus();
        GameSearchBox.CaretIndex = GameSearchBox.Text.Length;
        GameSearchBox.SelectedText = e.Text;
        GameSearchBox.CaretIndex = GameSearchBox.Text.Length;
        e.Handled = true;
    }

    private void GameSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // The search pill stays visible on Home and in Game Library; losing focus only removes the caret.
    }

    private void CloseGameSearch()
    {
        GameSearchBox.Text = string.Empty;
        GameSearchHost.Visibility = Visibility.Visible;
        var source = _applicationsSelected
            ? ApplicationPageSource(_activeApplicationFilter).ToList()
            : _allGames.Where(game => !game.IsHidden).ToList();
        if (_applicationsSelected) SetApplicationResults(source);
        else GamesList.ItemsSource = source;
        SetEmptyLibrary(source.Count == 0, _applicationsSelected ? "NO APPLICATIONS FOUND" : "NO GAMES FOUND",
            _applicationsSelected ? "Choose another application category." : "Refresh the library to scan again.");
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void ShowControllerCalibration_Click(object sender, RoutedEventArgs e)
    {
        ControllerSettingsPanel.Visibility = Visibility.Collapsed;
        ControllerCalibrationPanel.Visibility = Visibility.Visible;
        var key = _controller.ConnectedControllerName;
        if (!_inputSettings.Controllers.TryGetValue(key, out var profile))
        {
            profile = new ControllerProfile();
            _inputSettings.Controllers[key] = profile;
        }
        _editingControllerProfile = profile;
        _controller.ApplyProfile(profile);
        var choices = Enum.GetValues<ControllerButton>();
        var pickers = ControllerMappingPickers();
        foreach (var (command, picker) in pickers)
        {
            picker.ItemsSource = choices;
            picker.SelectedItem = profile.Bindings.GetValueOrDefault(command);
        }
        RefreshCalibrationControls();
        ControllerCalibrationPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private Dictionary<ControllerCommand, ComboBox> ControllerMappingPickers() => new()
    {
        [ControllerCommand.Accept] = AcceptButtonPicker,
        [ControllerCommand.Back] = BackButtonPicker,
        [ControllerCommand.PreviousTab] = PreviousTabButtonPicker,
        [ControllerCommand.NextTab] = NextTabButtonPicker,
        [ControllerCommand.Settings] = SettingsButtonPicker,
        [ControllerCommand.Power] = PowerButtonPicker
    };

    private void DecreaseDeadZone_Click(object sender, RoutedEventArgs e)
    {
        _editingControllerProfile.DeadZonePercent = Math.Max(5, _editingControllerProfile.DeadZonePercent - 5);
        _controller.ApplyProfile(_editingControllerProfile); RefreshCalibrationControls();
    }

    private void IncreaseDeadZone_Click(object sender, RoutedEventArgs e)
    {
        _editingControllerProfile.DeadZonePercent = Math.Min(60, _editingControllerProfile.DeadZonePercent + 5);
        _controller.ApplyProfile(_editingControllerProfile); RefreshCalibrationControls();
    }

    private void VibrationToggle_Click(object sender, RoutedEventArgs e)
    {
        _editingControllerProfile.VibrationEnabled = !_editingControllerProfile.VibrationEnabled;
        _controller.ApplyProfile(_editingControllerProfile); RefreshCalibrationControls();
    }

    private void TestVibration_Click(object sender, RoutedEventArgs e) => _controller.TestVibration();

    private void SaveControllerCalibration_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (command, picker) in ControllerMappingPickers())
            if (picker.SelectedItem is ControllerButton button) _editingControllerProfile.Bindings[command] = button;
        _inputSettings.Controllers[_controller.ConnectedControllerName] = _editingControllerProfile;
        ControllerSettingsStore.Save(_inputSettings);
        _controller.ApplyProfile(_editingControllerProfile);
        StatusText.Text = "CONTROLLER PROFILE SAVED";
        CancelControllerCalibration_Click(sender, e);
    }

    private void CancelControllerCalibration_Click(object sender, RoutedEventArgs e)
    {
        ControllerCalibrationPanel.Visibility = Visibility.Collapsed;
        ControllerSettingsPanel.Visibility = Visibility.Visible;
        ControllerSettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void RefreshCalibrationControls()
    {
        DeadZoneText.Text = $"{_editingControllerProfile.DeadZonePercent}%";
        VibrationToggleButton.Content = _editingControllerProfile.VibrationEnabled ? "VIBRATION: ON" : "VIBRATION: OFF";
        UpdateControllerCalibrationDisplay();
    }

    private void UpdateControllerCalibrationDisplay()
    {
        if (TaskSwitcherOverlay.Visibility == Visibility.Visible) UpdateTaskControllerPrompts();
        if (_controller.IsConnected != _lastControllerConnected)
        {
            if (IsLoaded) ShowNotification(_controller.IsConnected
                ? $"Controller connected: {_controller.ConnectedControllerName}"
                : "Controller disconnected");
            _lastControllerConnected = _controller.IsConnected;
        }
        if (_controller.IsConnected && !_controller.ConnectedControllerName.Equals(
                _appliedControllerName, StringComparison.OrdinalIgnoreCase))
        {
            _appliedControllerName = _controller.ConnectedControllerName;
            if (_inputSettings.Controllers.TryGetValue(_appliedControllerName, out var savedProfile))
                _controller.ApplyProfile(savedProfile);
        }
        if (!IsInitialized || ControllerCalibrationPanel.Visibility != Visibility.Visible) return;
        DetectedControllerText.Text = _controller.ConnectedControllerName.ToUpperInvariant();
        ControllerTestText.Text = $"{_controller.LastInput.ToUpperInvariant()}    STICK  X {_controller.LeftXPercent,4}%  Y {_controller.LeftYPercent,4}%";
    }

    private void LoadMetadataSettings()
    {
        _metadataSettings = MetadataSettingsStore.Load();
        PrimarySourcePicker.ItemsSource = _metadataSettings.Sources;
        PrimarySourcePicker.SelectedItem = _metadataSettings.Sources
            .FirstOrDefault(source => source.Id == _metadataSettings.PrimarySourceId)
            ?? _metadataSettings.Sources.FirstOrDefault();
    }

    private void PrimarySourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrimarySourcePicker.SelectedItem is not MetadataSourceConfig source) return;
        _metadataSettings.PrimarySourceId = source.Id;
        MetadataSettingsStore.Save(_metadataSettings);
        StatusText.Text = $"PRIMARY METADATA SOURCE: {source.Name.ToUpperInvariant()}";
    }

    private void ShowAddSource_Click(object sender, RoutedEventArgs e)
    {
        MetadataSettingsPanel.Visibility = Visibility.Collapsed;
        AddMetadataSourcePanel.Visibility = Visibility.Visible;
        NewSourceName.Focus();
    }

    private void CancelAddSource_Click(object sender, RoutedEventArgs e)
    {
        ClearNewSourceFields();
        AddMetadataSourcePanel.Visibility = Visibility.Collapsed;
        MetadataSettingsPanel.Visibility = Visibility.Visible;
        PrimarySourcePicker.Focus();
    }

    private void ShowRemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (_metadataSettings.Sources.Count == 0)
        {
            StatusText.Text = "NO METADATA SOURCES TO REMOVE";
            return;
        }
        RemoveSourcesList.ItemsSource = _metadataSettings.Sources;
        RemoveSourcesList.SelectedItem = null;
        MetadataSettingsPanel.Visibility = Visibility.Collapsed;
        RemoveSourceListPanel.Visibility = Visibility.Visible;
        RemoveSourcesList.Focus();
    }

    private void RemoveSourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoveSourcesList.SelectedItem is not MetadataSourceConfig source) return;
        RemoveConfirmationText.Text = $"Are you sure you want to remove {source.Name} source?";
        RemoveSourceListPanel.Visibility = Visibility.Collapsed;
        RemoveMetadataSourcePanel.Visibility = Visibility.Visible;
        RemoveMetadataSourcePanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void CancelRemoveSourceList_Click(object sender, RoutedEventArgs e)
    {
        RemoveSourceListPanel.Visibility = Visibility.Collapsed;
        MetadataSettingsPanel.Visibility = Visibility.Visible;
        PrimarySourcePicker.Focus();
    }

    private void CancelRemoveSource_Click(object sender, RoutedEventArgs e)
    {
        RemoveMetadataSourcePanel.Visibility = Visibility.Collapsed;
        RemoveSourceListPanel.Visibility = Visibility.Visible;
        RemoveSourcesList.SelectedItem = null;
        RemoveSourcesList.Focus();
    }

    private void ConfirmRemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (RemoveSourcesList.SelectedItem is not MetadataSourceConfig source) return;
        try
        {
            WindowsCredentialStore.DeleteMetadataKey(source.Id);
            _metadataSettings.Sources.RemoveAll(item => item.Id == source.Id);
            _metadataSettings.PrimarySourceId = _metadataSettings.Sources.FirstOrDefault()?.Id ?? string.Empty;
            MetadataSettingsStore.Save(_metadataSettings);
            LoadMetadataSettings();
            RemoveMetadataSourcePanel.Visibility = Visibility.Collapsed;
            MetadataSettingsPanel.Visibility = Visibility.Visible;
            StatusText.Text = $"{source.Name.ToUpperInvariant()} SOURCE REMOVED";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"FAILED TO REMOVE SOURCE: {exception.Message}";
        }
    }

    private void SaveSource_Click(object sender, RoutedEventArgs e)
    {
        var name = NewSourceName.Text.Trim();
        var url = NewSourceUrl.Text.Trim();
        var clientId = NewSourceClientId.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "A SOURCE NAME IS REQUIRED";
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var sourceUri) ||
            (sourceUri.Scheme != Uri.UriSchemeHttps && sourceUri.Scheme != Uri.UriSchemeHttp))
        {
            StatusText.Text = "ENTER A VALID HTTP OR HTTPS API URL";
            return;
        }
        if (string.IsNullOrWhiteSpace(NewSourceApiKey.Password))
        {
            StatusText.Text = "AN API KEY IS REQUIRED";
            return;
        }
        if (url.Contains("igdb.com", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(clientId))
        {
            StatusText.Text = "A CLIENT ID IS REQUIRED FOR IGDB";
            return;
        }

        try
        {
            var existing = _metadataSettings.Sources.FirstOrDefault(source =>
                string.Equals(source.BaseUrl?.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            var sourceId = existing?.Id ?? $"custom-{Guid.NewGuid():N}";
            WindowsCredentialStore.SaveMetadataKey(sourceId, NewSourceApiKey.Password);
            if (existing is null)
                _metadataSettings.Sources.Add(new MetadataSourceConfig
                {
                    Id = sourceId,
                    Name = name,
                    BaseUrl = url.TrimEnd('/'),
                    ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId
                });
            else
            {
                existing.Name = name;
                existing.BaseUrl = url.TrimEnd('/');
                existing.ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId;
                existing.IsEnabled = true;
            }
            MetadataSettingsStore.Save(_metadataSettings);
            ClearNewSourceFields();
            LoadMetadataSettings();
            AddMetadataSourcePanel.Visibility = Visibility.Collapsed;
            MetadataSettingsPanel.Visibility = Visibility.Visible;
            StatusText.Text = $"{name.ToUpperInvariant()} SOURCE SAVED";
        }
        catch (Exception exception)
        {
            ClearNewSourceFields();
            StatusText.Text = $"FAILED TO SAVE SOURCE: {exception.Message}";
        }
    }

    private void SettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsOverlay)) CloseSettings();
    }

    private void SettingsPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OverlayContent_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void TaskSwitcherContent_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        CloseTaskSwitcher();
        e.Handled = true;
    }

    private void DismissibleOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (ReferenceEquals(sender, ConnectionsOverlay)) ConnectionsOverlay.Visibility = Visibility.Collapsed;
        else if (ReferenceEquals(sender, TaskSwitcherOverlay)) CloseTaskSwitcher();
        else if (ReferenceEquals(sender, ErrorLogOverlay)) CloseErrorLog();
        else if (ReferenceEquals(sender, MetadataCorrectionOverlay))
            CancelMetadataCorrection_Click(this, new RoutedEventArgs());
    }

    private void CloseSettings()
    {
        ClearNewSourceFields();
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsButton.Tag = null;
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void ClearNewSourceFields()
    {
        NewSourceName.Clear();
        NewSourceUrl.Clear();
        NewSourceClientId.Clear();
        NewSourceApiKey.Clear();
    }

    private async void RefreshGames_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        ScanProgress.Value = 0;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = _applicationsSelected ? "SCANNING FOR APPLICATIONS" : "SCANNING FOR GAMES";
        var progress = new Progress<int>(value =>
        {
            ScanProgress.Value = value;
        });
        var scanStatus = new Progress<string>(message => StatusText.Text = message);

        try
        {
            if (_applicationsSelected)
            {
                ScanProgress.Value = 20;
                var applications = await Task.Run(ApplicationScanner.Scan);
                ScanProgress.Value = 75;
                _applications = FilterGameShortcuts(applications, _allGames);
                lock (ShortcutResolveSync) ShortcutResolveCache.Clear();
                UpdateApplicationRunningStates();
                RefreshQuickLaunchItems();
                ApplyApplicationFilter();
                ScanProgress.Value = 100;
                ShowNotification($"{_applications.Count} applications detected");
                return;
            }

            var knownGames = _allGames.Select(game => game.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var games = await Task.Run(() => GameLibrary.Load(progress));
            var newGames = games.Count(game => !knownGames.Contains(game.Target));
            await MetadataEnrichmentService.EnrichAsync(games, progress, scanStatus);
            DisplayGames(games);
            _applications = FilterGameShortcuts(ApplicationScanner.Scan(), games);
            RefreshQuickLaunchItems();
            ShowNotification(newGames > 0
                ? $"{newGames} new game{(newGames == 1 ? string.Empty : "s")} detected • Metadata download completed"
                : "Metadata download completed");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"GAME SCAN FAILED: {exception.Message}";
        }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanProgress.Value = 0;
            RefreshButton.IsEnabled = true;
        }
    }

    private void PowerOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PowerOverlay)) ClosePowerOptions();
    }

    private void PowerPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ClosePowerOptions()
    {
        PowerOverlay.Visibility = Visibility.Collapsed;
        PowerButton.Tag = null;
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void DesktopMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            StatusText.Text = "SWITCHING TO DESKTOP MODE";
            AllowShellClose = true;
            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"FAILED TO START DESKTOP MODE: {exception.Message}";
            ClosePowerOptions();
        }
    }

    private void LinuxMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var linuxBootEntry = FindLinuxFirmwareBootEntry();
            if (linuxBootEntry is null)
            {
                StatusText.Text = "NO LINUX UEFI BOOT ENTRY FOUND";
                ClosePowerOptions();
                return;
            }

            // Set the matching UEFI entry for the next boot only. Windows remains
            // the normal default on subsequent starts.
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c bcdedit /set {{fwbootmgr}} bootsequence {linuxBootEntry} /addfirst && shutdown /r /t 0",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StatusText.Text = "SWITCH TO LINUX CANCELLED";
            ClosePowerOptions();
        }
        catch
        {
            StatusText.Text = "UNABLE TO SWITCH TO LINUX";
            ClosePowerOptions();
        }
    }

    private static string? FindLinuxFirmwareBootEntry()
    {
        using var process = Process.Start(new ProcessStartInfo("bcdedit.exe", "/enum firmware")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0) return null;

        var matches = Regex.Matches(output,
            @"identifier\s+(\{[0-9a-f-]+\})[\s\S]*?description\s+([^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var linuxNames = new[]
        {
            "linux", "ubuntu", "fedora", "debian", "arch", "manjaro", "pop!_os",
            "pop os", "mint", "opensuse", "endeavouros", "grub"
        };
        return matches.Cast<Match>()
            .Where(match => linuxNames.Any(name => match.Groups[2].Value.Contains(name,
                StringComparison.OrdinalIgnoreCase)))
            .Select(match => match.Groups[1].Value)
            .FirstOrDefault();
    }

    private void CloseLauncher_Click(object sender, RoutedEventArgs e)
    {
        AllowShellClose = true;
        Close();
    }

    private void Shutdown_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false });

    private void Sleep_Click(object sender, RoutedEventArgs e)
    {
        if (!SetSuspendState(false, false, false)) StatusText.Text = "UNABLE TO ENTER SLEEP MODE";
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (!LockWorkStation()) StatusText.Text = "UNABLE TO LOCK WINDOWS";
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ConnectionsOverlay.Visibility == Visibility.Visible)
        {
            ConnectionsOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return;
        }
        if (e.Key == Key.Escape && TaskSwitcherOverlay.Visibility == Visibility.Visible)
        {
            CloseTaskSwitcher();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ErrorLogOverlay.Visibility == Visibility.Visible)
        {
            CloseErrorLog();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && MetadataCorrectionOverlay.Visibility == Visibility.Visible)
        {
            CancelMetadataCorrection_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.E && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            AllowShellClose = true;
            Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && GameSearchHost.Visibility == Visibility.Visible &&
            (GameSearchBox.IsKeyboardFocusWithin || !string.IsNullOrWhiteSpace(GameSearchBox.Text)))
        {
            CloseGameSearch();
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Escape || e.Key == Key.BrowserBack) &&
            GameDetailsPage.Visibility == Visibility.Visible)
        {
            CloseGameDetails();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape) e.Handled = true;
        if (e.Key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) e.Handled = true;
    }

    private void HandleControllerCommand(ControllerCommand command)
    {
        ActivateNonMouseInput();
        if (_inputSettings.NavigationAudioEnabled)
            NavigationSound.Play(command, _inputSettings.NavigationAudioVolume);
        switch (command)
        {
            case ControllerCommand.Left:
            case ControllerCommand.Right:
            case ControllerCommand.Up:
            case ControllerCommand.Down:
                NavigateController(command);
                break;
            case ControllerCommand.Accept:
                if (Keyboard.FocusedElement is Button button && button.IsEnabled)
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                else if (Keyboard.FocusedElement is ComboBox comboBox)
                    comboBox.IsDropDownOpen = !comboBox.IsDropDownOpen;
                break;
            case ControllerCommand.Back:
                ControllerBack();
                break;
            case ControllerCommand.PreviousTab:
                ShowTaskSwitcher();
                break;
            case ControllerCommand.NextTab:
                SelectControllerTab(true);
                break;
            case ControllerCommand.Settings:
                if (SettingsOverlay.Visibility != Visibility.Visible)
                    Settings_Click(SettingsButton, new RoutedEventArgs());
                break;
            case ControllerCommand.Power:
                if (PowerOverlay.Visibility != Visibility.Visible)
                    PowerOptions_Click(PowerButton, new RoutedEventArgs());
                break;
        }
    }

    private void NavigateController(ControllerCommand command)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        var modalRoot = ActiveModalRoot();
        if (modalRoot is not null)
        {
            var controls = FindVisualChildren<Control>(modalRoot)
                .Where(control => control.Focusable && KeyboardNavigation.GetIsTabStop(control) &&
                                  control.IsVisible && control.IsEnabled)
                .ToList();
            if (controls.Count == 0) return;
            var current = focused is null ? -1 : controls.FindIndex(control =>
                ReferenceEquals(control, focused) || IsAncestorOf(control, focused));
            var offset = command is ControllerCommand.Right or ControllerCommand.Down ? 1 : -1;
            var next = current < 0 ? (offset > 0 ? 0 : controls.Count - 1) :
                (current + offset + controls.Count) % controls.Count;
            controls[next].Focus();
            controls[next].BringIntoView();
            return;
        }
        if (_homeSelected)
        {
            var quickButtons = FindVisualChildren<Button>(QuickLaunchList)
                .Where(button => button.IsVisible && button.IsEnabled).ToList();
            var onContinue = focused is not null &&
                (ReferenceEquals(ContinueButton, focused) || IsAncestorOf(ContinueButton, focused));
            var quickIndex = focused is null ? -1 : quickButtons.FindIndex(button =>
                ReferenceEquals(button, focused) || IsAncestorOf(button, focused));
            if (command is ControllerCommand.Up or ControllerCommand.Down)
            {
                if (onContinue && quickButtons.Count > 0) quickButtons[0].Focus();
                else if (quickIndex >= 0 && ContinuePlayingPanel.Visibility == Visibility.Visible) ContinueButton.Focus();
                RememberHomeFocus();
                return;
            }
            if (quickIndex >= 0 && command is ControllerCommand.Left or ControllerCommand.Right)
            {
                var offset = command == ControllerCommand.Right ? 1 : -1;
                quickButtons[(quickIndex + offset + quickButtons.Count) % quickButtons.Count].Focus();
                RememberHomeFocus();
                return;
            }
        }
        var activeLibraryList = _applicationsSelected ? ApplicationList : GamesList;
        var gameButtons = FindVisualChildren<Button>(activeLibraryList)
            .Where(button => button.IsVisible && button.IsEnabled).ToList();
        var gameIndex = focused is null ? -1 : gameButtons.FindIndex(button =>
            ReferenceEquals(button, focused) || IsAncestorOf(button, focused));
        if (gameIndex >= 0 && command is ControllerCommand.Left or ControllerCommand.Right)
        {
            var offset = command == ControllerCommand.Right ? 1 : -1;
            var next = (gameIndex + offset + gameButtons.Count) % gameButtons.Count;
            gameButtons[next].Focus();
            gameButtons[next].BringIntoView();
            return;
        }

        var direction = command switch
        {
            ControllerCommand.Left => FocusNavigationDirection.Left,
            ControllerCommand.Right => FocusNavigationDirection.Right,
            ControllerCommand.Up => FocusNavigationDirection.Up,
            _ => FocusNavigationDirection.Down
        };
        if (Keyboard.FocusedElement is UIElement element &&
            element.MoveFocus(new TraversalRequest(direction))) return;

        var root = PowerOverlay.Visibility == Visibility.Visible ? PowerOverlay :
            ErrorLogOverlay.Visibility == Visibility.Visible ? ErrorLogOverlay :
            ConnectionsOverlay.Visibility == Visibility.Visible ? ConnectionsOverlay :
            SettingsOverlay.Visibility == Visibility.Visible ? SettingsOverlay :
            GameDetailsPage.Visibility == Visibility.Visible ? GameDetailsPage : (DependencyObject)this;
        var focusable = FindVisualChildren<Control>(root)
            .Where(control => control.Focusable && control.IsVisible && control.IsEnabled).ToList();
        if (focusable.Count == 0) return;
        (command is ControllerCommand.Left or ControllerCommand.Up ? focusable[^1] : focusable[0]).Focus();
    }

    private DependencyObject? ActiveModalRoot()
    {
        if (PowerOverlay.Visibility == Visibility.Visible) return PowerOverlay;
        if (ErrorLogOverlay.Visibility == Visibility.Visible) return ErrorLogOverlay;
        if (MetadataCorrectionOverlay.Visibility == Visibility.Visible) return MetadataCorrectionOverlay;
        if (ConnectionsOverlay.Visibility == Visibility.Visible) return ConnectionsOverlay;
        if (SettingsOverlay.Visibility == Visibility.Visible) return SettingsOverlay;
        if (TaskSwitcherOverlay.Visibility == Visibility.Visible) return TaskSwitcherOverlay;
        return null;
    }

    private void RememberHomeFocus()
    {
        if (!_homeSelected || Keyboard.FocusedElement is not DependencyObject focused) return;
        if (ReferenceEquals(ContinueButton, focused) || IsAncestorOf(ContinueButton, focused))
            _lastHomeSelection = "Continue";
        else
        {
            var button = FindAncestor<Button>(focused);
            if (button?.Tag is GameEntry application) _lastHomeSelection = application.Name;
        }
    }

    private void ShowTaskSwitcher()
    {
        if (TaskSwitcherOverlay.Visibility == Visibility.Visible)
        {
            var buttons = FindVisualChildren<Button>(TaskWindowList).Where(button => button.IsVisible).ToList();
            if (buttons.Count > 1)
            {
                var current = Keyboard.FocusedElement as DependencyObject;
                var index = current is null ? -1 : buttons.FindIndex(button =>
                    ReferenceEquals(button, current) || IsAncestorOf(button, current));
                buttons[(index + 1 + buttons.Count) % buttons.Count].Focus();
            }
            return;
        }

        Show();
        WindowState = WindowState.Maximized;
        Activate();
        Topmost = true;
        Dispatcher.BeginInvoke(() => Topmost = false, DispatcherPriority.ApplicationIdle);
        var shellHandle = new WindowInteropHelper(this).Handle;
        var windows = GetTaskWindows(shellHandle);

        TaskWindowList.ItemsSource = windows;
        UpdateTaskControllerPrompts();
        TaskSwitcherOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            RegisterTaskThumbnails();
            FindVisualChildren<Button>(TaskWindowList).FirstOrDefault()?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private static List<TaskWindowEntry> GetTaskWindows(IntPtr shellHandle)
    {
        var windows = new List<TaskWindowEntry>();
        EnumWindows((handle, _) =>
        {
            if (!IsTaskSwitcherWindow(handle, shellHandle)) return true;
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

    private void CloseTaskSwitcher()
    {
        foreach (var thumbnail in _taskThumbnails) DwmUnregisterThumbnail(thumbnail);
        _taskThumbnails.Clear();
        TaskSwitcherOverlay.Visibility = Visibility.Collapsed;
        TaskWindowList.ItemsSource = null;
    }

    private static bool IsTaskSwitcherWindow(IntPtr handle, IntPtr shellHandle)
    {
        // Do not reject a window just because it has an owner. WinUI, Chromium and other
        // modern desktop applications frequently use owned top-level windows for their
        // primary UI (ChatGPT is one example).
        if (handle == shellHandle || !IsWindowVisible(handle)) return false;
        if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var extendedStyle = GetWindowLong(handle, -20);
        if ((extendedStyle & 0x08000000) != 0) return false;
        if (!GetWindowRect(handle, out var rectangle) || rectangle.Right - rectangle.Left < 100 ||
            rectangle.Bottom - rectangle.Top < 80) return false;
        var className = new System.Text.StringBuilder(256);
        GetClassName(handle, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
            return false;
        return true;
    }

    private void TaskWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskWindowEntry window }) return;
        ActivateTaskWindow(window);
    }

    private void HomeTaskWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskWindowEntry window }) return;
        CloseHomeTaskWindows();
        ShowWindow(window.Handle, 9);
        SetForegroundWindow(window.Handle);
    }

    private void CloseHomeTaskWindows()
    {
        foreach (var thumbnail in _homeTaskThumbnails) DwmUnregisterThumbnail(thumbnail);
        _homeTaskThumbnails.Clear();
        HomeTaskWindowsHost.Visibility = Visibility.Collapsed;
        HomeTaskWindowList.ItemsSource = null;
    }

    private void ActivateFocusedTaskWindow()
    {
        if (TaskSwitcherOverlay.Visibility != Visibility.Visible) return;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var button = focused is null ? null : FindAncestor<Button>(focused);
        if (button?.Tag is TaskWindowEntry window) ActivateTaskWindow(window);
        else if (TaskWindowList.ItemsSource is IEnumerable<TaskWindowEntry> windows && windows.FirstOrDefault() is { } first)
            ActivateTaskWindow(first);
        else CloseTaskSwitcher();
    }

    private void ActivateTaskWindow(TaskWindowEntry window)
    {
        CloseTaskSwitcher();
        Topmost = false;
        ShowWindow(window.Handle, 9);
        BringWindowToTop(window.Handle);
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(window.Handle, out _);
        var currentThread = GetCurrentThreadId();
        try
        {
            if (foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, true);
            if (targetThread != currentThread) AttachThreadInput(currentThread, targetThread, true);
            SetForegroundWindow(window.Handle);
            SetFocus(window.Handle);
        }
        finally
        {
            if (targetThread != currentThread) AttachThreadInput(currentThread, targetThread, false);
            if (foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private void RegisterTaskThumbnails()
    {
        foreach (var thumbnail in _taskThumbnails) DwmUnregisterThumbnail(thumbnail);
        _taskThumbnails.Clear();
        var destination = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var preview in FindVisualChildren<Border>(TaskWindowList)
                     .Where(border => border.Tag is TaskWindowEntry && border.IsVisible))
        {
            var window = (TaskWindowEntry)preview.Tag;
            if (DwmRegisterThumbnail(destination, window.Handle, out var thumbnail) != 0 || thumbnail == IntPtr.Zero)
                continue;
            var point = preview.TransformToAncestor(this).Transform(new Point(0, 0));
            var properties = new DwmThumbnailProperties
            {
                Flags = 0x1 | 0x4 | 0x8 | 0x10,
                Destination = new NativeRect
                {
                    Left = (int)Math.Round(point.X * dpi.DpiScaleX),
                    Top = (int)Math.Round(point.Y * dpi.DpiScaleY),
                    Right = (int)Math.Round((point.X + preview.ActualWidth) * dpi.DpiScaleX),
                    Bottom = (int)Math.Round((point.Y + preview.ActualHeight) * dpi.DpiScaleY)
                },
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = false
            };
            DwmUpdateThumbnailProperties(thumbnail, ref properties);
            _taskThumbnails.Add(thumbnail);
        }
    }

    private void RegisterHomeTaskThumbnails()
    {
        foreach (var thumbnail in _homeTaskThumbnails) DwmUnregisterThumbnail(thumbnail);
        _homeTaskThumbnails.Clear();
        var destination = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var preview in FindVisualChildren<Border>(HomeTaskWindowList)
                     .Where(border => border.Tag is TaskWindowEntry && border.IsVisible))
        {
            var window = (TaskWindowEntry)preview.Tag;
            if (DwmRegisterThumbnail(destination, window.Handle, out var thumbnail) != 0 || thumbnail == IntPtr.Zero)
                continue;
            var point = preview.TransformToAncestor(this).Transform(new Point(0, 0));
            var properties = new DwmThumbnailProperties
            {
                Flags = 0x1 | 0x4 | 0x8 | 0x10,
                Destination = new NativeRect
                {
                    Left = (int)Math.Round(point.X * dpi.DpiScaleX), Top = (int)Math.Round(point.Y * dpi.DpiScaleY),
                    Right = (int)Math.Round((point.X + preview.ActualWidth) * dpi.DpiScaleX),
                    Bottom = (int)Math.Round((point.Y + preview.ActualHeight) * dpi.DpiScaleY)
                },
                Opacity = 255, Visible = true, SourceClientAreaOnly = false
            };
            DwmUpdateThumbnailProperties(thumbnail, ref properties);
            _homeTaskThumbnails.Add(thumbnail);
        }
    }

    private void UpdateTaskControllerPrompts()
    {
        var controllerActive = _inputSettings.InputMethod == "Controller" && _controller.IsConnected;
        TaskControllerPrompts.Visibility = controllerActive ? Visibility.Visible : Visibility.Collapsed;
        if (!controllerActive) return;

        var profile = _inputSettings.Controllers.TryGetValue(_controller.ConnectedControllerName, out var saved)
            ? saved
            : new ControllerProfile();
        var accept = profile.Bindings.GetValueOrDefault(ControllerCommand.Accept, ControllerButton.A);
        var back = profile.Bindings.GetValueOrDefault(ControllerCommand.Back, ControllerButton.B);
        TaskAcceptButtonGlyph.Text = ControllerButtonGlyph(accept, _controller.ConnectedControllerName);
        TaskBackButtonGlyph.Text = ControllerButtonGlyph(back, _controller.ConnectedControllerName);
    }

    private static string ControllerButtonGlyph(ControllerButton button, string controllerName)
    {
        var playStation = controllerName.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) ||
                          controllerName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
                          controllerName.Contains("DualShock", StringComparison.OrdinalIgnoreCase) ||
                          controllerName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase);
        if (playStation)
            return button switch
            {
                ControllerButton.A => "✕", ControllerButton.B => "○", ControllerButton.X => "□",
                ControllerButton.Y => "△", ControllerButton.LeftShoulder => "L1",
                ControllerButton.RightShoulder => "R1", ControllerButton.Menu => "OPT", _ => "SHR"
            };
        var nintendo = controllerName.Contains("Nintendo", StringComparison.OrdinalIgnoreCase) ||
                       controllerName.Contains("Switch", StringComparison.OrdinalIgnoreCase) ||
                       controllerName.Contains("Joy-Con", StringComparison.OrdinalIgnoreCase);
        if (nintendo)
            return button switch
            {
                ControllerButton.A => "B", ControllerButton.B => "A", ControllerButton.X => "Y",
                ControllerButton.Y => "X", ControllerButton.LeftShoulder => "L",
                ControllerButton.RightShoulder => "R", ControllerButton.Menu => "+", _ => "−"
            };
        return button switch
        {
            ControllerButton.LeftShoulder => "LB", ControllerButton.RightShoulder => "RB",
            ControllerButton.Menu => "≡", ControllerButton.View => "▣", _ => button.ToString()
        };
    }

    private void ControllerBack()
    {
        if (ConnectionsOverlay.Visibility == Visibility.Visible) ConnectionsOverlay.Visibility = Visibility.Collapsed;
        else if (TaskSwitcherOverlay.Visibility == Visibility.Visible) CloseTaskSwitcher();
        else if (ErrorLogOverlay.Visibility == Visibility.Visible) CloseErrorLog();
        else if (MetadataCorrectionOverlay.Visibility == Visibility.Visible)
            CancelMetadataCorrection_Click(this, new RoutedEventArgs());
        else if (GameSearchHost.Visibility == Visibility.Visible &&
                 (GameSearchBox.IsKeyboardFocusWithin || !string.IsNullOrWhiteSpace(GameSearchBox.Text))) CloseGameSearch();
        else if (GameDetailsPage.Visibility == Visibility.Visible) CloseGameDetails();
        else if (PowerOverlay.Visibility == Visibility.Visible) ClosePowerOptions();
        else if (SettingsOverlay.Visibility == Visibility.Visible &&
                 (ControllerSettingsPanel.Visibility == Visibility.Visible ||
                  MetadataSettingsPanel.Visibility == Visibility.Visible ||
                  NetworkSettingsPanel.Visibility == Visibility.Visible))
            BackToSettingsHome_Click(this, new RoutedEventArgs());
        else if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        else if (!_homeSelected) ShowHomePage();
    }

    private void SelectControllerTab(bool applications)
    {
        _homeSelected = false;
        _applicationsSelected = applications;
        _homeHoverLocked = false;
        GamesList.IsHitTestVisible = true;
        LibraryScroller.Visibility = applications ? Visibility.Collapsed : Visibility.Visible;
        ApplicationScroller.Visibility = applications ? Visibility.Visible : Visibility.Collapsed;
        ContinuePlayingPanel.Visibility = Visibility.Collapsed;
        QuickLaunchHost.Visibility = applications ? Visibility.Collapsed : Visibility.Visible;
        PerformanceModeHost.Visibility = applications ? Visibility.Collapsed : Visibility.Visible;
        LibraryFilters.Visibility = applications ? Visibility.Collapsed : Visibility.Visible;
        ApplicationFilters.Visibility = applications ? Visibility.Visible : Visibility.Collapsed;
        ApplicationPagination.Visibility = applications ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(RefreshButton,
            applications ? "Rescan applications" : "Rescan games");
        GameSearchBox.Text = string.Empty;
        GameSearchHost.Visibility = Visibility.Visible;
        GameSearchHost.Margin = applications ? new Thickness(0, 0, 0, 578) : new Thickness(0, 0, 0, 349);
        GameLibraryHeader.Foreground = applications ? CreateFrozenBrush("#8A8A8A") : Brushes.White;
        ApplicationsHeader.Foreground = applications ? Brushes.White : CreateFrozenBrush("#8A8A8A");
        if (applications) UpdateApplicationRunningStates();
        var items = applications
            ? _applications.OrderByDescending(app => app.IsRunning).ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : _allGames.Where(game => !game.IsHidden).ToList();
        if (applications) ApplicationList.ItemsSource = items;
        else GamesList.ItemsSource = items;
        SetEmptyLibrary(items.Count == 0,
            applications ? "NO APPLICATIONS FOUND" : "NO GAMES FOUND",
            applications ? "No supported Quick Launch applications were detected." : "Refresh the library to scan again.");
        StatusText.Text = applications ? $"{items.Count} APPLICATIONS READY" : $"{items.Count} GAMES READY";
        if (applications) ApplyApplicationFilter();
        else ApplyLibraryFilters();
        var firstGame = FindVisualChildren<Button>(applications ? ApplicationList : GamesList).FirstOrDefault(button => button.IsVisible);
        if (!applications && firstGame is not null) firstGame.Focus();
    }

    private void GameLibraryHeader_Click(object sender, MouseButtonEventArgs e) => SelectControllerTab(false);
    private void ApplicationsHeader_Click(object sender, MouseButtonEventArgs e) => SelectControllerTab(true);

    private void OmenBrand_Click(object sender, MouseButtonEventArgs e) => ShowHomePage();

    private void ShowHomePage()
    {
        CloseHomeTaskWindows();
        if (GameDetailsPage.Visibility == Visibility.Visible) CloseGameDetails();
        if (PowerOverlay.Visibility == Visibility.Visible) ClosePowerOptions();
        if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        GameSearchBox.Text = string.Empty;
        _homeSelected = false;
        _applicationsSelected = false;
        _homeHoverLocked = false;
        GamesList.IsHitTestVisible = true;
        LibraryScroller.Visibility = Visibility.Visible;
        ApplicationScroller.Visibility = Visibility.Collapsed;
        ContinuePlayingPanel.Visibility = Visibility.Collapsed;
        GameLibraryHeader.Foreground = Brushes.White;
        ApplicationsHeader.Foreground = CreateFrozenBrush("#8A8A8A");
        GamesList.ItemsSource = _allGames.Where(game => !game.IsHidden).ToList();
        GameSearchHost.Visibility = Visibility.Visible;
        QuickLaunchHost.Visibility = Visibility.Visible;
        PerformanceModeHost.Visibility = Visibility.Visible;
        LibraryFilters.Visibility = Visibility.Visible;
        ApplicationFilters.Visibility = Visibility.Collapsed;
        ApplicationPagination.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(RefreshButton, "Rescan games");
        var visibleGameCount = _allGames.Count(game => !game.IsHidden);
        SetEmptyLibrary(visibleGameCount == 0, "NO GAMES FOUND", "Refresh the library to scan again.");
        ApplyLibraryFilters();
        HideGameBackground();
        StatusText.Text = $"{visibleGameCount} GAMES READY";
        Keyboard.ClearFocus();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T result) yield return result;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = TreeParent(child);
        }
        return null;
    }

    private static bool IsAncestorOf(DependencyObject ancestor, DependencyObject child)
    {
        for (var current = child; current is not null; current = TreeParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static DependencyObject? TreeParent(DependencyObject? child)
    {
        if (child is Visual or Visual3D) return VisualTreeHelper.GetParent(child);
        return LogicalTreeHelper.GetParent(child);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = !AllowShellClose;
        if (!e.Cancel)
        {
            _windowSource?.RemoveHook(WindowMessageHook);
            _controller.Dispose();
            _gameSession.Dispose();
            _keyboardGuard?.Dispose();
        }
        base.OnClosing(e);
    }

    private bool AllowShellClose { get; set; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maximumCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
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
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecute(IntPtr window, string operation, string file,
        string? parameters, string? directory, int showCommand);
    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destinationWindow, IntPtr sourceWindow, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DwmThumbnailProperties properties);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("BluetoothApis.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams parameters, out IntPtr radioHandle);
    [DllImport("BluetoothApis.dll", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr findHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothFindRadioParams
    {
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }
}
