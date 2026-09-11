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
    private readonly DispatcherTimer _coverExitTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _wifiScanTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _deviceScanTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _taskViewHotCornerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private const string UpdateNotificationTag = "update";
    private const string UpdateNotificationIcon = "\uE895";
    private const string IconBatteryLow = "\uE9EE";
    private const string IconBatteryCritical = "\uE9ED";
    private const string BatteryNotificationTag = "battery";
    private const int BatteryWarnPercent = 20;
    private const int BatteryCriticalPercent = 10;
    private const int BatteryShutdownPercent = 5;
    private bool _battery20Notified;
    private bool _battery10Notified;
    private BatteryCriticalOverlay? _batteryOverlay;
    private readonly DispatcherTimer _updateCheckTimer = new() { Interval = TimeSpan.FromHours(1) };
    private bool _updateCheckInProgress;
    private bool _updateDismissedThisSession;
    private UpdateReleaseInfo? _pendingRelease;
    private readonly ControllerInput _controller = new();
    private MetadataSettings _metadataSettings = new();
    private BackgroundSettings _backgroundSettings = new();
    private bool _customBackgroundActive;
    private InputSettings _inputSettings = new();
    private ControllerProfile _editingControllerProfile = new();
    private string _appliedControllerName = string.Empty;
    private ShellKeyboardGuard? _keyboardGuard;
    private readonly GameSessionManager _gameSession = new();
    private string? _pendingCorrectedCover;
    private string? _pendingCorrectedBackground;
    private IReadOnlyList<GameEntry> _allGames = Array.Empty<GameEntry>();
    private bool _homeSelected;
    private bool _homeHoverLocked;
    private GameEntry? _selectedDetailsGame;
    private bool _lastControllerConnected;
    private string _activeLibraryFilter = "All";
    private HwndSource? _windowSource;
    private string? _lastHomeSelection;
    private WinKeyOverlayWindow? _winKeyOverlay;
    private readonly List<IntPtr> _homeTaskThumbnails = new();
    private WifiNetwork? _pendingWifiNetwork;
    private IReadOnlyList<BluetoothDevice> _cachedBluetoothDevices = Array.Empty<BluetoothDevice>();
    private bool _wifiScanInProgress;
    private IReadOnlyList<TaskWindowEntry> _altTabWindows = Array.Empty<TaskWindowEntry>();
    private int _altTabIndex;
    private IntPtr _altTabThumbnail;
    private readonly List<IntPtr> _altTabStripThumbnails = new();
    private bool _altTabActive;
    private IntPtr _previousForegroundWindow;
    private bool _suppressFocusRestore;
    private bool _gameCoverHovered;
    private bool _gameCoverActive;
    private readonly DispatcherTimer _bgSlideshowTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _networkCheckTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DispatcherTimer? _autoRescanTimer;
    private bool _autoRescanRunning;
    private bool _lastNetworkAvailable = true;
    private int _bgSlideshowIndex;
    private IReadOnlyList<GameEntry> _slideshowGames = Array.Empty<GameEntry>();
    private Button? _mouseFocusedButton;
    private Button? _mouseSuppressedButton;
    private DependencyObject? _lastMouseMoveSource;
    private readonly List<string> _backupFolders = new();
    private bool _automaticBackupEnabled;
    private string? _backupDriveRoot;
    private bool _backupInProgress;
    private static string BackupSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "backup-settings.json");

    public MainWindow()
    {
        InitializeComponent();
        PreviewTextInput += MainWindow_PreviewTextInput;
        _controller.Command += HandleControllerCommand;
        _controller.AcceptHeld += HandleControllerAcceptHeld;
        _controller.StateChanged += UpdateControllerCalibrationDisplay;
        _controller.StateChanged += UpdateControllerIndicator;
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
            if (_gameCoverActive) return;
            _gameCoverHovered = false;
            StartSlideshow();
            SlideInContainer(ContinueHost);
            SlideInContainer(PerformanceModeHost);
            SlideInContainer(MediaControlsBar);
            SlideInContainer(NotificationsHost);
        };
        _wifiScanTimer.Tick += async (_, _) =>
        {
            if (ConnectionsOverlay.Visibility == Visibility.Visible &&
                WifiConnectionsPanel.Visibility == Visibility.Visible)
                await RefreshConnectionsAsync(true, false);
            else
                _wifiScanTimer.Stop();
        };
        _deviceScanTimer.Tick += async (_, _) => { await RefreshConnectedDevicesAsync(); };
        _taskViewHotCornerTimer.Tick += (_, _) =>
        {
            _taskViewHotCornerTimer.Stop();
            if (!IsWinKeyOverlayOpen) OpenWinKeyOverlay();
        };
        _bgSlideshowTimer.Tick += (_, _) => AdvanceSlideshow();
        _networkCheckTimer.Tick += (_, _) => UpdateNetworkStatus();
        _networkCheckTimer.Start();
        UpdateNetworkStatus();
        _autoRescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _autoRescanTimer.Tick += async (_, _) => await AutoRescanGamesAsync();
        _autoRescanTimer.Start();
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdateAsync(silent: true);
        _updateCheckTimer.Start();
        InitFeatures();
        _clockTimer.Start();
        UpdateClock();
        LoadInputSettings();
        LoadBackgroundSettings();
        LoadMetadataSettings();
        _ = LoadGamesAsync();
        RefreshErrorLog();
        _ = RefreshConnectedDevicesAsync();
        _deviceScanTimer.Start();
        _ = PreloadBluetoothDevicesAsync();
        Loaded += async (_, _) =>
        {
            if (_keyboardGuard is null)
            {
                var windowHandle = new WindowInteropHelper(this).Handle;
                _controller.SetShellHandle(windowHandle);
                _keyboardGuard = new ShellKeyboardGuard(windowHandle);
                _windowSource = HwndSource.FromHwnd(windowHandle);
                _windowSource?.AddHook(WindowMessageHook);
                _keyboardGuard.WindowsKeyPressed += () => Dispatcher.BeginInvoke(HandleWindowsKey);
                _keyboardGuard.AltTabPressed += () => Dispatcher.BeginInvoke(ShowAltTabOverlay);
                _keyboardGuard.AltTabRepeated += () => Dispatcher.BeginInvoke(CycleAltTabSelection);
                _keyboardGuard.AltReleased += () => Dispatcher.BeginInvoke(CompleteAltTab);
            }
            UpdateTaskControllerPrompts();
            LoadBackupSettings();
            RefreshBackupDrives();
            UpdateBackupStatus();
            // Wait for games to finish loading if not done yet, then display
            for (var retry = 0; retry < 20 && GamesList.ItemsSource is not IReadOnlyList<GameEntry> { Count: > 0 }; retry++)
                await Task.Delay(250);
            if (GamesList.ItemsSource is IReadOnlyList<GameEntry> games)
            {
                await MetadataEnrichmentService.EnrichAsync(games);
                DisplayGames(games);
                ShowHomePage();
            }
            try
            {
                var bootTask = RunBootAnimationAsync();
                var timeout = Task.Delay(4000);
                await Task.WhenAny(bootTask, timeout);
            }
            catch { }
            LibraryArea.IsHitTestVisible = true;
            LibraryArea.Opacity = 1;
            ShellTopStatus.Opacity = 1;
            GameLibraryHeaderHost.Opacity = 1;
            OmenBrand.Opacity = 1;
            BrandTranslation.X = 0;
            BrandTranslation.Y = 0;
            BootBrandScale.ScaleX = 1;
            BootBrandScale.ScaleY = 1;
            _ = RunStartupUpdateCheckAsync();
        };
    }

    private async Task RefreshConnectedDevicesAsync()
    {
        UpdateBackupStatus();
        RefreshBackupDrives();
        if (!_automaticBackupEnabled || _backupInProgress || _backupFolders.Count == 0 || string.IsNullOrWhiteSpace(_backupDriveRoot) || !Directory.Exists(_backupDriveRoot)) return;
        _backupInProgress = true;
        try { await Task.Run(() => RunBackup(_backupDriveRoot)); }
        catch { }
        finally { _backupInProgress = false; }
        UpdateBackupStatus();
    }

    private void UpdateBackupStatus()
    {
        var driveConnected = !string.IsNullOrWhiteSpace(_backupDriveRoot) && Directory.Exists(_backupDriveRoot);
        var backupMarker = driveConnected ? Path.Combine(_backupDriveRoot!, "Backup", "last-backup.txt") : string.Empty;
        var backedUp = driveConnected && File.Exists(backupMarker);
        var setupDone = _backupFolders.Count > 0 && !string.IsNullOrWhiteSpace(_backupDriveRoot);
        if (!setupDone) SetBackupStatus(null, string.Empty);
        else if (!driveConnected) SetBackupStatus("DRIVE OFFLINE", "CONNECT THE SELECTED BACKUP DRIVE");
        else if (_backupInProgress) SetBackupStatus("UPDATING", "SAVING YOUR LATEST CHANGES");
        else if (backedUp) SetBackupStatus("UPDATED", $"LAST SAVED {File.GetLastWriteTime(backupMarker):dd MMM yyyy HH:mm}".ToUpperInvariant());
        else SetBackupStatus("PENDING", "FIRST BACKUP HAS NOT RUN YET");
        BackupLocationText.Text = driveConnected ? "BACKUP DRIVE READY" : "BACKUP DRIVE OFFLINE";
        BackupSelectedDriveText.Text = string.IsNullOrWhiteSpace(_backupDriveRoot) ? "NO DRIVE SELECTED" : $"SELECTED DRIVE: {_backupDriveRoot}" + (driveConnected ? string.Empty : "  (NOT CONNECTED)");
        BackupSetupButton.Visibility = setupDone ? Visibility.Collapsed : Visibility.Visible;
        EjectDriverButton.Visibility = driveConnected && setupDone ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBackupStatus(string? status, string detail)
    {
        var hasStatus = !string.IsNullOrEmpty(status);
        BackupStatusDot.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
        BackupStatusText.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
        BackupLastRunText.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
        BackupStatusText.Text = status ?? string.Empty;
        BackupLastRunText.Text = detail;
        BackupStatusDot.Fill = CreateFrozenBrush(status == "UPDATED" ? "#38D878" : status == "DRIVE OFFLINE" ? "#FF003C" : status == "UPDATING" ? "#FFC928" : "#888888");
    }

    private void BackupSetup_Click(object sender, RoutedEventArgs e) => OpenBackupSettings();

    private async void EjectDriver_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_backupDriveRoot)) return;
        try
        {
            var driveLetter = _backupDriveRoot.TrimEnd('\\', '/');
            SetBackupStatus("EJECTING", "SAFELY REMOVING THE DRIVE");
            var success = await Task.Run(() => EjectDrive(driveLetter));
            if (success)
            {
                Notify($"Drive {driveLetter} ejected safely", "Drive ejected", $"{driveLetter} was ejected safely", IconEject);
                _backupDriveRoot = null;
                SaveBackupSettings();
                RefreshBackupDrives();
            }
            else
            {
                ShowNotification("Could not eject drive — close any open files and try again");
            }
            UpdateBackupStatus();
        }
        catch (Exception exception)
        {
            ShowNotification($"Eject failed: {exception.Message}");
            UpdateBackupStatus();
        }
    }

    private static bool EjectDrive(string driveLetter)
    {
        try
        {
            var letter = driveLetter.TrimEnd('\\', '/').Last();
            var script = $@"Get-Disk | Where-Object {{ $_.Number -eq (Get-Volume -DriveLetter '{letter}' -ErrorAction SilentlyContinue).DiskNumber }} | ForEach-Object {{ $_.IsOffline = $true; $_.OfflineReason = 1 }}";
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = $"-NoProfile -NonInteractive -Command \"{script}\""
            });
            if (process is null) return false;
            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private sealed class BackupDriveEntry
    {
        public string Root { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }

    private void LoadBackupSettings()
    {
                _backupFolders.Clear();
try
        {
            if (!File.Exists(BackupSettingsPath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(BackupSettingsPath));
            if (document.RootElement.TryGetProperty("folders", out var folders))
                _backupFolders.AddRange(folders.EnumerateArray().Select(item => item.GetString()).OfType<string>().Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase));
            if (document.RootElement.TryGetProperty("automatic", out var automatic)) _automaticBackupEnabled = automatic.GetBoolean();
            if (document.RootElement.TryGetProperty("drive", out var drive)) _backupDriveRoot = drive.GetString();
        }
        catch { }
        BackupFoldersList.ItemsSource = _backupFolders;
        AutomaticBackupToggle.IsChecked = _automaticBackupEnabled;
    }

    private void SaveBackupSettings()
    {
        var folder = Path.GetDirectoryName(BackupSettingsPath)!;
        Directory.CreateDirectory(folder);
        var payload = new { folders = _backupFolders, automatic = _automaticBackupEnabled, drive = _backupDriveRoot };
        File.WriteAllText(BackupSettingsPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RefreshBackupDrives()
    {
        var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady && (drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.Fixed))
            .Select(drive => new BackupDriveEntry { Root = drive.RootDirectory.FullName, DisplayName = $"{drive.VolumeLabel} ({drive.RootDirectory.FullName.TrimEnd('\\')})" })
            .OrderBy(entry => entry.Root).ToList();
        BackupDrivePicker.ItemsSource = drives;
        BackupDrivePicker.SelectedItem = drives.FirstOrDefault(entry => string.Equals(entry.Root, _backupDriveRoot, StringComparison.OrdinalIgnoreCase));
        if (BackupDrivePicker.SelectedItem is BackupDriveEntry selected) _backupDriveRoot = selected.Root;
        UpdateBackupStatus();
    }

    private void BackupStatus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenBackupSettings();
    }

    private void AccessoriesContainer_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenAccessoriesOverlay();
    }

    private async void OpenAccessoriesOverlay()
    {
        AccessoriesOverlay.Visibility = Visibility.Visible;
        SlideIn(AccessoriesOverlay, 0, 24, 280);
        try
        {
            var devices = await Task.Run(() => ConnectedDeviceScanner.Scan());
            _ = Dispatcher.BeginInvoke(() => AccessoriesFullList.ItemsSource = devices);
        }
        catch { _ = Dispatcher.BeginInvoke(() => AccessoriesFullList.ItemsSource = Array.Empty<ConnectedDeviceEntry>()); }
    }

    private void CloseAccessoriesOverlay(object sender, RoutedEventArgs e)
    {
        AccessoriesOverlay.Visibility = Visibility.Collapsed;
    }

    private async void RefreshAccessoriesOverlay(object sender, RoutedEventArgs e)
    {
        try
        {
            var devices = await Task.Run(() => ConnectedDeviceScanner.Scan());
            _ = Dispatcher.BeginInvoke(() => AccessoriesFullList.ItemsSource = devices);
        }
        catch { }
    }

    private void OpenBackupSettings()
    {
        LoadBackupSettings();
        RefreshBackupDrives();
        BackupSettingsOverlay.Visibility = Visibility.Visible;
        BackupSettingsOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void BackupSettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)) CloseBackupSettings();
    }

    private void BackupSettingsPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void BackupSettingsBack_Click(object sender, RoutedEventArgs e) => CloseBackupSettings();

    private void CloseBackupSettings()
    {
        BackupSettingsOverlay.Visibility = Visibility.Collapsed;
        SaveBackupSettings();
        UpdateBackupStatus();
    }

    private void AddBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to back up", Multiselect = false };
        if (dialog.ShowDialog(this) != true || _backupFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) return;
        _backupFolders.Add(dialog.FolderName);
        BackupFoldersList.ItemsSource = null;
        BackupFoldersList.ItemsSource = _backupFolders;
        SaveBackupSettings();
    }

    private void RemoveBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (BackupFoldersList.SelectedItem is not string folder) return;
        _backupFolders.Remove(folder);
        BackupFoldersList.ItemsSource = null;
        BackupFoldersList.ItemsSource = _backupFolders;
        SaveBackupSettings();
    }

    private void AutomaticBackupToggle_Click(object sender, RoutedEventArgs e)
    {
        _automaticBackupEnabled = AutomaticBackupToggle.IsChecked == true;
        SaveBackupSettings();
        BackupSettingsMessage.Text = _automaticBackupEnabled ? "AUTOMATIC BACKUP ENABLED" : "AUTOMATIC BACKUP DISABLED";
    }

    private void BackupDrivePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BackupDrivePicker.SelectedItem is BackupDriveEntry drive)
        {
            _backupDriveRoot = drive.Root;
            SaveBackupSettings();
        }
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (_backupFolders.Count == 0) { BackupSettingsMessage.Text = "ADD AT LEAST ONE FOLDER FIRST"; return; }
        if (string.IsNullOrWhiteSpace(_backupDriveRoot) || !Directory.Exists(_backupDriveRoot)) { BackupSettingsMessage.Text = "CONNECT AND SELECT A BACKUP DRIVE"; return; }
        BackupSettingsMessage.Text = "BACKING UP...";
        SetBackupStatus("UPDATING", "COPYING SELECTED FOLDERS");
        try
        {
            await Task.Run(() => RunBackup(_backupDriveRoot));
            BackupSettingsMessage.Text = "BACKUP COMPLETE";
            UpdateBackupStatus();
        }
        catch (Exception exception) { BackupSettingsMessage.Text = $"BACKUP FAILED: {exception.Message}"; }
    }

    private void RunBackup(string driveRoot)
    {
        var destination = Path.Combine(driveRoot, "Backup");
        Directory.CreateDirectory(destination);
        foreach (var source in _backupFolders.Where(Directory.Exists))
        {
            var target = Path.Combine(destination, new DirectoryInfo(source).Name);
            CopyDirectory(source, target);
        }
        File.WriteAllText(Path.Combine(destination, "last-backup.txt"), DateTime.UtcNow.ToString("O"));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
    private void ErrorLog_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (PowerOverlay.Visibility == Visibility.Visible) ClosePowerOptions();
        if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        RefreshErrorLog();
        ErrorLogOverlay.Visibility = Visibility.Visible;
        SlideIn(ErrorLogOverlay, 0, 24, 280);
        ErrorLogOverlay.UpdateLayout();
        ErrorLogButton.Tag = "OverlayOpen";
        Keyboard.Focus(NewErrorText);
    }

    private void CloseErrorLog_Click(object sender, RoutedEventArgs e) => CloseErrorLog();

    private async void CloseErrorLog()
    {
        await AnimateOutAsync(ErrorLogOverlay, 0, 18, 180);
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
        GameLibraryHeaderHost.Opacity = 0;
        ShellTopStatus.Opacity = 0;
        LibraryArea.Opacity = 0;
        ShellControls.Opacity = 0;

        await AnimateAsync(OmenBrand, UIElement.OpacityProperty, 0, 1, 400);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var moveX = AnimateAsync(BrandTranslation, TranslateTransform.XProperty,
            BrandTranslation.X, 0, 900, easing);
        var moveY = AnimateAsync(BrandTranslation, TranslateTransform.YProperty,
            BrandTranslation.Y, 0, 900, easing);
        var scaleX = AnimateAsync(BootBrandScale, ScaleTransform.ScaleXProperty, 1.8, 1, 900, easing);
        var scaleY = AnimateAsync(BootBrandScale, ScaleTransform.ScaleYProperty, 1.8, 1, 900, easing);
        var reveal = Task.WhenAll(
            AnimateAsync(GameLibraryHeaderHost, UIElement.OpacityProperty, 0, 1, 600),
            AnimateAsync(ShellTopStatus, UIElement.OpacityProperty, 0, 1, 600),
            AnimateAsync(LibraryArea, UIElement.OpacityProperty, 0, 1, 600),
            AnimateAsync(ShellControls, UIElement.OpacityProperty, 0, 1, 600));
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
            FillBehavior = FillBehavior.HoldEnd
        };
        // When the animation ends we MUST drop its clock (BeginAnimation(null)) so the
        // animated value no longer overrides the property. Otherwise a held value (e.g. a
        // grid slid off-screen, or opacity faded to 0) sticks forever and later direct
        // assignments like transform.X = 0 / Opacity = 1 are silently ignored — leaving
        // the game/app grid invisible after it has slid out once.
        void Settle()
        {
            switch (target)
            {
                case UIElement uiTarget:
                    uiTarget.BeginAnimation(property, null);
                    break;
                case Animatable animatable:
                    animatable.BeginAnimation(property, null);
                    break;
            }
            target.SetValue(property, to);
        }
        animation.Completed += (_, _) =>
        {
            Settle();
            completion.TrySetResult();
        };
        if (target is UIElement element)
            element.BeginAnimation(property, animation);
        else if (target is Animatable animatable)
            animatable.BeginAnimation(property, animation);
        else
        {
            completion.TrySetResult();
            return Task.CompletedTask;
        }
        _ = Task.Delay(milliseconds + 200).ContinueWith(_ =>
        {
            Settle();
            completion.TrySetResult();
        });
        return completion.Task;
    }

    private static async Task AnimateInAsync(UIElement element, double fromX = 0, double fromY = 0,
        int milliseconds = 300)
    {
        var transform = element.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }
        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = false;
        element.Opacity = 0;
        transform.X = fromX;
        transform.Y = fromY;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        await Task.WhenAll(
            AnimateAsync(element, UIElement.OpacityProperty, 0, 1, milliseconds, easing),
            AnimateAsync(transform, TranslateTransform.XProperty, fromX, 0, milliseconds, easing),
            AnimateAsync(transform, TranslateTransform.YProperty, fromY, 0, milliseconds, easing));
        element.IsHitTestVisible = true;
    }

    private static async Task AnimateOutAsync(UIElement element, double toX = 0, double toY = 0,
        int milliseconds = 210)
    {
        if (element.Visibility != Visibility.Visible) return;
        var transform = element.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }
        element.IsHitTestVisible = false;
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        await Task.WhenAll(
            AnimateAsync(element, UIElement.OpacityProperty, element.Opacity, 0, milliseconds, easing),
            AnimateAsync(transform, TranslateTransform.XProperty, transform.X, toX, milliseconds, easing),
            AnimateAsync(transform, TranslateTransform.YProperty, transform.Y, toY, milliseconds, easing));
        element.Visibility = Visibility.Collapsed;
        element.Opacity = 1;
        transform.X = 0;
        transform.Y = 0;
    }

    private static void SlideIn(UIElement element, double fromX = 0, double fromY = 0, int milliseconds = 300) =>
        _ = AnimateInAsync(element, fromX, fromY, milliseconds);

    private static void SlideOut(UIElement element, double toX = 0, double toY = 0, int milliseconds = 210) =>
        _ = AnimateOutAsync(element, toX, toY, milliseconds);

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
        if (inTaskViewCorner && !IsWinKeyOverlayOpen)
        {
            if (!_taskViewHotCornerTimer.IsEnabled) _taskViewHotCornerTimer.Start();
        }
        else
        {
            _taskViewHotCornerTimer.Stop();
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
        var button = ReferenceEquals(e.OriginalSource, _lastMouseMoveSource) ? _mouseFocusedButton : FindAncestor<Button>(e.OriginalSource as DependencyObject);
        _lastMouseMoveSource = e.OriginalSource as DependencyObject;
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

    private async Task LoadGamesAsync()
    {
        try
        {
            var games = await Task.Run(() => GameLibrary.Load());
            _ = Dispatcher.BeginInvoke(() => DisplayGames(games));
        }
        catch (Exception exception)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                EmptyLibrary.Visibility = Visibility.Visible;
                StatusText.Text = exception.Message.ToUpperInvariant();
            });
        }
    }

    private async Task AutoRescanGamesAsync()
    {
        if (_autoRescanRunning || LibraryArea.Opacity < 1) return;
        _autoRescanRunning = true;
        try
        {
            var knownGames = _allGames.Select(game => game.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var games = await Task.Run(() => GameLibrary.Load());
            var newGames = games.Where(game => !knownGames.Contains(game.Target)).ToList();
            if (newGames.Count == 0) return;

            await MetadataEnrichmentService.EnrichAsync(newGames);
            if (LibraryArea.Opacity < 1) return;

            _allGames = games;
            DisplayGames(games);
            var howMany = newGames.Count == 1 ? "1 new game" : $"{newGames.Count} new games";
            Notify($"{howMany} detected", "New games found", $"{howMany} detected in your library", IconAdd);
        }
        catch
        {
        }
        finally
        {
            _autoRescanRunning = false;
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
        InitSlideshow();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("dddd, dd MMMM").ToUpperInvariant();
        UpdateBattery();
    }

    private void UpdateNetworkStatus()
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
    }

    private void UpdateBattery()
    {
        var percent = PowerStatus.BatteryPercent();
        if (PowerStatus.IsCharging())
        {
            _battery20Notified = false;
            _battery10Notified = false;
            NotificationCenter.RemoveTag(BatteryNotificationTag);
            HideBatteryCriticalOverlay();
            BatteryFill.Width = percent is null ? 0 : 16d * percent.Value / 100d;
            BatteryFill.Background = BatteryGreen;
            ChargingIcon.Visibility = Visibility.Visible;
            return;
        }

        ChargingIcon.Visibility = Visibility.Collapsed;
        if (percent is null)
        {
            BatteryFill.Width = 0;
            return;
        }

        var percentage = percent.Value;
        BatteryFill.Width = 16d * percentage / 100d;
        BatteryFill.Background = percentage > 50
            ? BatteryGreen
            : percentage > 20 ? BatteryYellow : BatteryRed;

        if (percentage <= BatteryShutdownPercent)
        {
            ShowBatteryCriticalOverlay(percentage);
        }
        else
        {
            HideBatteryCriticalOverlay();
        }

        if (percentage <= BatteryWarnPercent && !_battery20Notified)
        {
            _battery20Notified = true;
            NotificationCenter.RemoveTag(BatteryNotificationTag);
            Notify($"Battery lower than {BatteryWarnPercent}%", "Battery low", "Connect a power source soon.", IconBatteryLow, BatteryNotificationTag);
        }

        if (percentage <= BatteryCriticalPercent && !_battery10Notified)
        {
            _battery10Notified = true;
            NotificationCenter.RemoveTag(BatteryNotificationTag);
            Notify($"Battery lower than {BatteryCriticalPercent}%", "Battery low", "Battery is almost drained. Plug in your charger now.", IconBatteryCritical, BatteryNotificationTag);
        }
    }

    private void ShowBatteryCriticalOverlay(int percent)
    {
        if (_batteryOverlay is null)
        {
            _batteryOverlay = new BatteryCriticalOverlay();
            _batteryOverlay.Closed += (_, _) => _batteryOverlay = null;
        }
        _batteryOverlay.ShowOverlay(percent, BatteryShutdownPercent);
    }

    private void HideBatteryCriticalOverlay()
    {
        _batteryOverlay?.Dismiss();
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void Game_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameEntry game }) return;
        OpenGameDetails(game);
    }

    private void OpenGameDetails(GameEntry game)
    {
        _selectedDetailsGame = game;
        GameDetailsPage.DataContext = game;
        HideGameBackground();
        GameDetailsPage.Visibility = Visibility.Visible;
        SlideIn(GameDetailsPage, 70, 0, 340);
        LaunchDetailsButton.Focus();
    }

    private void ContinueGame_Click(object sender, RoutedEventArgs e)
    {
        _lastHomeSelection = "Continue";
        if (ContinueHost.DataContext is GameEntry game) OpenGameDetails(game);
    }

    private IntPtr WindowMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        return IntPtr.Zero;
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
        if (_homeSelected) return;
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

    private const string IconNetwork = "\uE701";
    private const string IconEject = "\uE72D";
    private const string IconAdd = "\uE8B7";
    private const string IconAudio = "\uE7F5";

    private void Notify(string toast, string title, string message, string icon = "\uE7BA", string? tag = null)
    {
        ShowNotification(toast);
        NotificationCenter.Push(title, message, icon, tag);
    }

    private string CurrentAppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private async Task RunStartupUpdateCheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        await CheckForUpdateAsync(silent: true);
    }

    private async Task CheckForUpdateAsync(bool silent)
    {
        if (_updateCheckInProgress || _updateDismissedThisSession) return;
        _updateCheckInProgress = true;
        try
        {
            var release = await UpdateChecker.FetchLatestReleaseAsync();
            if (release is null) return;
            _pendingRelease = release;
            if (!UpdateChecker.IsUpdateAvailable(CurrentAppVersion, release))
            {
                NotificationCenter.RemoveTag(UpdateNotificationTag);
                return;
            }
            if (UpdateChecker.IsVersionBlocked(release.Version))
            {
                NotificationCenter.RemoveTag(UpdateNotificationTag);
                return;
            }
            if (NotificationCenter.HasTag(UpdateNotificationTag))
                NotificationCenter.RemoveTag(UpdateNotificationTag);
            NotificationCenter.Push("Update available", $"v{release.Version} is ready to install", UpdateNotificationIcon, UpdateNotificationTag);
            ShowUpdatePrompt(release);
        }
        catch (Exception exception)
        {
            if (!silent) ShowNotification("Update check failed");
            ErrorLogStore.Log(exception.Message, "Update check", exception.ToString());
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private void ShowUpdatePrompt(UpdateReleaseInfo release)
    {
        _pendingRelease = release;
        UpdateBannerVersionText.Text = $"OMEN Gaming Shell v{release.Version} is available";
        ShowUpdateBanner();
    }

    private void ShowUpdateBanner()
    {
        if (UpdateBanner.Visibility == Visibility.Visible) return;
        UpdateBanner.Visibility = Visibility.Visible;
        var transform = UpdateBanner.RenderTransform as TranslateTransform ?? new TranslateTransform();
        UpdateBanner.RenderTransform = transform;
        transform.X = 420;
        UpdateBanner.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animX = new DoubleAnimation(420, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease };
        var animOpacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease };
        animOpacity.Completed += (_, _) => UpdateBanner.BeginAnimation(UIElement.OpacityProperty, null);
        UpdateBanner.BeginAnimation(UIElement.OpacityProperty, animOpacity);
        transform.BeginAnimation(TranslateTransform.XProperty, animX);
    }

    private void HideUpdateBanner()
    {
        if (UpdateBanner.Visibility != Visibility.Visible) return;
        var transform = UpdateBanner.RenderTransform as TranslateTransform ?? new TranslateTransform();
        UpdateBanner.RenderTransform = transform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var animX = new DoubleAnimation(0, 420, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd
        };
        var animOpacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd
        };
        animX.Completed += (_, _) =>
        {
            UpdateBanner.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            UpdateBanner.Visibility = Visibility.Collapsed;
        };
        animOpacity.Completed += (_, _) =>
        {
            UpdateBanner.BeginAnimation(UIElement.OpacityProperty, null);
            UpdateBanner.Opacity = 1;
        };
        UpdateBanner.BeginAnimation(UIElement.OpacityProperty, animOpacity);
        transform.BeginAnimation(TranslateTransform.XProperty, animX);
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        HideUpdateBanner();
        _updateDismissedThisSession = true;
        NotificationCenter.RemoveTag(UpdateNotificationTag);
        PopulateNotifications();
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        HideUpdateBanner();
        var release = _pendingRelease;
        if (release is null) return;
        UpdateProgressText.Text = $"v{release.Version} is downloading...";
        UpdateProgressOverlay.Visibility = Visibility.Visible;
        var ready = await UpdateChecker.DownloadAndApplyAsync(release);
        if (!ready)
        {
            UpdateProgressOverlay.Visibility = Visibility.Collapsed;
            ShowNotification("Update failed. Try again later");
            NotificationCenter.Push("Update failed", "The update could not be installed. Your current version is unaffected.", UpdateNotificationIcon);
            return;
        }
        Application.Current.Shutdown();
    }

    private async void OpenUpdateFromNotificationAsync()
    {
        NotificationCenterOverlay.Visibility = Visibility.Collapsed;
        var release = _pendingRelease;
        if (release is null)
        {
            release = await UpdateChecker.FetchLatestReleaseAsync();
            if (release is null || !UpdateChecker.IsUpdateAvailable(CurrentAppVersion, release)) return;
            _pendingRelease = release;
        }
        ShowUpdatePrompt(release);
    }

    private void UpdateContinuePlaying()
    {
        var game = _allGames
            .Where(entry => !entry.IsHidden && entry.LastPlayedUtc.HasValue)
            .OrderByDescending(entry => entry.LastPlayedUtc)
            .FirstOrDefault();

        ContinueHost.DataContext = game;

        if (game is null)
        {
            if (_homeSelected)
            {
                ContinueHost.Visibility = Visibility.Visible;
                ContinueContent.Visibility = Visibility.Collapsed;
                ContinueWelcomeText.Visibility = Visibility.Visible;
                ContinueCoverBrush.ImageSource = null;
            }
            else
            {
                ContinueHost.Visibility = Visibility.Collapsed;
            }
            return;
        }

        ContinueContent.Visibility = Visibility.Visible;
        ContinueWelcomeText.Visibility = Visibility.Collapsed;
        ContinueHost.Visibility = _homeSelected ? Visibility.Visible : Visibility.Collapsed;
        ContinueGameName.Text = game.Name.ToUpperInvariant();
        ContinueCoverBrush.ImageSource = LoadLocalImage(game.Cover);
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

    private async void CloseGameDetails()
    {
        await AnimateOutAsync(GameDetailsPage, 70, 0, 220);
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
            if (button?.Tag is not GameEntry || button.ContextMenu is null) return;
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
        SlideIn(MetadataCorrectionOverlay, 0, 22, 260);
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
        SlideOut(MetadataCorrectionOverlay, 0, 18, 170);
        GameDetailsPage.DataContext = null; GameDetailsPage.DataContext = _selectedDetailsGame;
        GamesList.Items.Refresh();
        StatusText.Text = "GAME METADATA SAVED";
        LaunchDetailsButton.Focus();
    }

    private void CancelMetadataCorrection_Click(object sender, RoutedEventArgs e)
    {
        SlideOut(MetadataCorrectionOverlay, 0, 18, 170);
        LaunchDetailsButton.Focus();
    }

    internal async void LaunchGame(GameEntry game)
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
            if (state == GameSessionState.Failed) Notify($"Game launch failed: {game.Name}", "Game launch failed", $"{game.Name} could not be launched");
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
        _gameCoverActive = true;
        ActivateGameLibraryFromHome();
        
        SlideOutContainer(PerformanceModeHost, 200);
        SlideOutContainer(MediaControlsBar, 200);
        SlideOutContainer(ContinueHost, 200);
        SlideOutContainer(NotificationsHost, 200);
        ShowGameBackground(sender);
    }

    private static void SlideOutContainer(UIElement element, int milliseconds = 200)
    {
        if (element.Visibility != Visibility.Visible) return;
        var transform = element.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var animX = new DoubleAnimation(0, 250, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd
        };
        var animOpacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd
        };
        animX.Completed += (_, _) =>
        {
            element.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            element.Visibility = Visibility.Collapsed;
        };
        animOpacity.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };
        element.BeginAnimation(UIElement.OpacityProperty, animOpacity);
        transform.BeginAnimation(TranslateTransform.XProperty, animX);
    }

    private static void SlideInContainer(UIElement element, int milliseconds = 250)
    {
        var transform = element.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }
        element.Visibility = Visibility.Visible;
        element.Opacity = 0;
        transform.X = 200;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animX = new DoubleAnimation(200, 0, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd
        };
        var animOpacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd
        };
        animOpacity.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };
        animX.Completed += (_, _) =>
        {
            element.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
        };
        element.BeginAnimation(UIElement.OpacityProperty, animOpacity);
        transform.BeginAnimation(TranslateTransform.XProperty, animX);
    }

    private void Game_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button { IsKeyboardFocusWithin: true }) Keyboard.ClearFocus();
        _gameCoverActive = false;
        _coverExitTimer.Stop();
        _coverExitTimer.Start();
    }

    private void Game_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _coverExitTimer.Stop();
        _gameCoverActive = true;
        ActivateGameLibraryFromHome();
        
        SlideOutContainer(PerformanceModeHost, 200);
        SlideOutContainer(MediaControlsBar, 200);
        SlideOutContainer(ContinueHost, 200);
        SlideOutContainer(NotificationsHost, 200);
        ShowGameBackground(sender);
    }

    private void ActivateGameLibraryFromHome()
    {
        if (!_homeSelected) return;
        _homeSelected = false;
        _homeHoverLocked = false;
        GamesList.IsHitTestVisible = true;
        ContinueHost.Visibility = Visibility.Collapsed;
        
        PerformanceModeHost.Visibility = Visibility.Visible;
        LibraryFilters.Visibility = Visibility.Visible;
        GameSearchBox.Text = string.Empty;
        GameSearchHost.Visibility = Visibility.Visible;
        GameSearchHost.Margin = new Thickness(0, 0, 0, 349);
        GameLibraryHeader.Foreground = Brushes.White;
        StatusText.Text = $"{_allGames.Count} GAMES READY";
    }

    private void Game_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button { IsMouseOver: true })
        {
            _gameCoverActive = false;
            _coverExitTimer.Stop();
            _coverExitTimer.Start();
        }
    }

    private void ShowGameBackground(object sender)
    {
        _gameCoverHovered = true;
        _bgSlideshowTimer.Stop();
        if (sender is not Button { Tag: GameEntry game })
        {
            StartSlideshow();
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

    private void InitSlideshow()
    {
        _slideshowGames = _allGames.Where(g => !g.IsHidden && !string.IsNullOrEmpty(g.Background)).ToList();
        _bgSlideshowIndex = 0;
        if (_slideshowGames.Count > 0 && !_gameCoverHovered)
            StartSlideshow();
    }

    private void StartSlideshow()
    {
        if (_slideshowGames.Count == 0 || _gameCoverHovered || _customBackgroundActive) return;
        _bgSlideshowTimer.Stop();
        AdvanceSlideshow();
        _bgSlideshowTimer.Start();
    }

    private void AdvanceSlideshow()
    {
        if (_gameCoverHovered || _customBackgroundActive || _slideshowGames.Count == 0 ||
            GameDetailsPage.Visibility == Visibility.Visible ||
            IsWinKeyOverlayOpen ||
            SettingsOverlay.Visibility == Visibility.Visible ||
            PowerOverlay.Visibility == Visibility.Visible ||
            ConnectionsOverlay.Visibility == Visibility.Visible)
        {
            _bgSlideshowTimer.Stop(); return;
        }
        var game = _slideshowGames[_bgSlideshowIndex % _slideshowGames.Count];
        _bgSlideshowIndex = (_bgSlideshowIndex + 1) % _slideshowGames.Count;
        var image = LoadLocalImage(game.Background);
        if (image is null) return;
        FallbackGameBackground.Source = image;
        FallbackGameBackground.Opacity = 0.35;
        GameBackgroundShade.Opacity = 1;
    }

    private void HideGameBackground()
    {
        if (_customBackgroundActive)
        {
            ApplyShellBackground();
            return;
        }
        if (!_gameCoverHovered && _slideshowGames.Count > 0)
        {
            StartSlideshow();
            return;
        }
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
        SlideIn(PowerOverlay, 0, 24, 280);
        PowerOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void PowerOverlayBack_Click(object sender, RoutedEventArgs e) => ClosePowerOptions();

    private void SettingsOverlayBack_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsHomePanel.Visibility == Visibility.Visible) { CloseSettings(); return; }
        if (BackgroundSettingsPanel.Visibility == Visibility.Visible)
        {
            BackgroundSettingsPanel.Visibility = Visibility.Collapsed;
            SettingsHomePanel.Visibility = Visibility.Visible;
            SettingsHomePanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }
        if (OsSettingsPanel.Visibility == Visibility.Visible) { OsSettingsPanel.Visibility = Visibility.Collapsed; SettingsHomePanel.Visibility = Visibility.Visible; SettingsHomePanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); return; }
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
        OsSettingsPanel.Visibility = Visibility.Collapsed;
        ControllerSettingsPanel.Visibility = Visibility.Collapsed;
        MetadataSettingsPanel.Visibility = Visibility.Collapsed;
        NetworkSettingsPanel.Visibility = Visibility.Collapsed;
        AddMetadataSourcePanel.Visibility = Visibility.Collapsed;
        RemoveSourceListPanel.Visibility = Visibility.Collapsed;
        RemoveMetadataSourcePanel.Visibility = Visibility.Collapsed;
        ControllerCalibrationPanel.Visibility = Visibility.Collapsed;
        BackgroundSettingsPanel.Visibility = Visibility.Collapsed;
        MetadataSettingsHomeButton.Visibility = Visibility.Visible;
        SettingsOverlay.Visibility = Visibility.Visible;
        SlideIn(SettingsOverlay, 0, 24, 300);
        SettingsOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void ShowOsSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsHomePanel.Visibility = Visibility.Collapsed;
        OsSettingsPanel.Visibility = Visibility.Visible;
        DefaultDesktopPicker.ItemsSource = DetectDesktopOptions();
        DefaultDesktopPicker.SelectedItem = _inputSettings.DefaultDesktop;
        OsSettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private static List<string> DetectDesktopOptions()
    {
        var options = new List<string>();
        var outputPath = Path.Combine(Path.GetTempPath(), "omen-bcd-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var command = $"Start-Process cmd.exe -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '/c bcdedit /enum all > \"{outputPath}\"'";
            using var scan = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{command}\"")
            {
                UseShellExecute = false, CreateNoWindow = true
            });
            scan?.WaitForExit(20000);
            if (!File.Exists(outputPath)) return options;
            var output = File.ReadAllText(outputPath);
            try { File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "bcd-info.txt"), output); } catch { }
            foreach (Match match in Regex.Matches(output, @"(?im)^\s*description\s+(.+?)\s*$"))
            {
                var name = match.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Windows Boot Manager", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Windows Recovery", StringComparison.OrdinalIgnoreCase) || name.Contains("Firmware", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Hard Disk", StringComparison.OrdinalIgnoreCase) || name.Contains("Resume", StringComparison.OrdinalIgnoreCase) || name.Contains("Memory Diagnostic", StringComparison.OrdinalIgnoreCase) || name.Contains("Memory Diagnostics", StringComparison.OrdinalIgnoreCase)) continue;
                var usable = new[] { "ubuntu", "fedora", "debian", "arch", "mint", "manjaro", "opensuse", "windows" }
                    .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
                if (usable && !options.Contains(name, StringComparer.OrdinalIgnoreCase)) options.Add(name);
            }
        }
        catch { }
        finally { try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { } }
        return options;
    }
    private void ScanOs_Click(object sender, RoutedEventArgs e)
    {
        var values = DetectDesktopOptions();
        DefaultDesktopPicker.ItemsSource = values;
        if (values.Contains(_inputSettings.DefaultDesktop, StringComparer.OrdinalIgnoreCase))
            DefaultDesktopPicker.SelectedItem = _inputSettings.DefaultDesktop;
        StatusText.Text = values.Count > 1 ? $"FOUND {values.Count - 1} OTHER DESKTOP(S)" : "ONLY WINDOWS WAS DETECTED";
    }
    private void AddOs_Click(object sender, RoutedEventArgs e)
    {
        var name = DefaultDesktopPicker.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        var values = DefaultDesktopPicker.Items.Cast<string>().ToList();
        if (!values.Contains(name, StringComparer.OrdinalIgnoreCase)) values.Add(name);
        DefaultDesktopPicker.ItemsSource = values;
        DefaultDesktopPicker.SelectedItem = name;
        _inputSettings.DefaultDesktop = name;
        ControllerSettingsStore.Save(_inputSettings);
    }
    private void DefaultDesktopPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultDesktopPicker.SelectedItem is not string desktop) return;
        _inputSettings.DefaultDesktop = desktop;
        ControllerSettingsStore.Save(_inputSettings);
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

    private void LoadBackgroundSettings()
    {
        _backgroundSettings = BackgroundSettingsStore.Load();
        ApplyShellBackground();
    }

    private void ShowBackgroundSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsHomePanel.Visibility = Visibility.Collapsed;
        BackgroundSettingsPanel.Visibility = Visibility.Visible;
        RefreshBackgroundChoiceList();
        BackgroundSettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void RefreshBackgroundChoiceList()
    {
        var choices = _allGames
            .Where(game => !game.IsHidden && !string.IsNullOrWhiteSpace(game.Background) && File.Exists(game.Background))
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .Select(game =>
            {
                var background = game.Background!;
                return new BackgroundChoice(game.Name, LoadLocalImage(background),
                    IsCurrentBackground(background), background);
            })
            .ToList();
        BackgroundChoiceList.ItemsSource = choices;
    }

    private bool IsCurrentBackground(string path)
    {
        if (!_backgroundSettings.UseCustomBackground) return false;
        return string.Equals(_backgroundSettings.CustomBackgroundPath, path, StringComparison.OrdinalIgnoreCase);
    }

    private void BrowseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a shell background image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        _backgroundSettings.CustomBackgroundPath = dialog.FileName;
        _backgroundSettings.UseCustomBackground = true;
        BackgroundSettingsStore.Save(_backgroundSettings);
        ApplyShellBackground();
        RefreshBackgroundChoiceList();
        StatusText.Text = "SHELL BACKGROUND UPDATED";
    }

    private void BackgroundChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackgroundChoice choice }) return;
        _backgroundSettings.CustomBackgroundPath = choice.Path;
        _backgroundSettings.UseCustomBackground = true;
        BackgroundSettingsStore.Save(_backgroundSettings);
        ApplyShellBackground();
        RefreshBackgroundChoiceList();
        StatusText.Text = $"SHELL BACKGROUND: {choice.Name.ToUpperInvariant()}";
    }

    private void ResetBackground_Click(object sender, RoutedEventArgs e)
    {
        _backgroundSettings.UseCustomBackground = false;
        _backgroundSettings.CustomBackgroundPath = null;
        BackgroundSettingsStore.Save(_backgroundSettings);
        ApplyShellBackground();
        RefreshBackgroundChoiceList();
        StatusText.Text = "BACKGROUND RESET TO AUTO SLIDESHOW";
    }

    private void ApplyShellBackground()
    {
        if (_backgroundSettings.UseCustomBackground &&
            !string.IsNullOrWhiteSpace(_backgroundSettings.CustomBackgroundPath) &&
            File.Exists(_backgroundSettings.CustomBackgroundPath))
        {
            var image = LoadLocalImage(_backgroundSettings.CustomBackgroundPath);
            if (image is not null)
            {
                _customBackgroundActive = true;
                _bgSlideshowTimer.Stop();
                _gameCoverHovered = false;
                FallbackGameBackground.Source = image;
                FallbackGameBackground.Opacity = 0.35;
                GameBackgroundShade.Opacity = 1;
                return;
            }
        }
        _customBackgroundActive = false;
        HideGameBackground();
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
        SlideIn(ConnectionsOverlay, 0, 24, 280);
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
            if (wifi) { WifiNetworksList.ItemsSource = await ConnectionService.ScanWifiAsync(); CheckListBoxArrows(); }
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
        try { await ConnectionService.ConnectWifiAsync(network, WifiPasswordBox.Password); Notify($"Connected to {network.Name}", "Wi-Fi connected", $"Connected to {network.Name}", IconNetwork); }
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
                Notify($"Disconnected from {network.Name}", "Wi-Fi disconnected", $"Disconnected from {network.Name}", IconNetwork);
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
                Notify($"Connected to {network.Name}", "Wi-Fi connected", $"Connected to {network.Name}", IconNetwork);
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
            Notify($"Connected to {network.Name}", "Wi-Fi connected", $"Connected to {network.Name}", IconNetwork);
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
        if (paired)
            Notify($"{device.Name} paired", "Device paired", $"{device.Name} was paired successfully");
        else
            ShowNotification($"Could not pair {device.Name}");
        await RefreshConnectionsAsync(false);
    }
    private async void RemoveBluetooth_Click(object sender, RoutedEventArgs e)
    {
        if (BluetoothDevicesList.SelectedItem is not BluetoothDevice device) return;
        var removed = await Task.Run(() => ConnectionService.RemoveBluetooth(device));
        if (removed)
            Notify($"{device.Name} removed", "Device removed", $"{device.Name} was removed");
        else
            ShowNotification($"Could not remove {device.Name}");
        await RefreshConnectionsAsync(false);
    }
    private async void BluetoothDeviceAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BluetoothDevice device }) return;
        bool success;
        if (device.Connected) success = await Task.Run(() => ConnectionService.DisconnectBluetooth(device));
        else success = await Task.Run(() => ConnectionService.ConnectBluetooth(device));
        ShowNotification(success
            ? device.Connected ? $"{device.Name} disconnected" : $"{device.Name} connected"
            : $"Could not {(device.Connected ? "disconnect" : "connect")} {device.Name}");
        await Task.Delay(1000);
        await RefreshConnectionsAsync(false);
    }
    private void CloseConnections_Click(object sender, RoutedEventArgs e)
    {
        _wifiScanTimer.Stop();
        SlideOut(ConnectionsOverlay, 0, 18, 180);
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
        OsSettingsPanel.Visibility = Visibility.Collapsed;
        ControllerSettingsPanel.Visibility = Visibility.Collapsed;
        MetadataSettingsPanel.Visibility = Visibility.Collapsed;
        NetworkSettingsPanel.Visibility = Visibility.Collapsed;
        ControllerCalibrationPanel.Visibility = Visibility.Collapsed;
        AddMetadataSourcePanel.Visibility = Visibility.Collapsed;
        RemoveSourceListPanel.Visibility = Visibility.Collapsed;
        RemoveMetadataSourcePanel.Visibility = Visibility.Collapsed;
        BackgroundSettingsPanel.Visibility = Visibility.Collapsed;
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
        ShowButtonGuideToggle.IsChecked = _inputSettings.ShowButtonGuide;
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
        try
        {
            if (sender is not Button { Tag: string mode }) return;
            if (mode is not ("Ultimate" or "Balanced" or "Eco")) return;
            _inputSettings.PerformanceMode = mode;
            UpdatePerformanceModeButtons();
            try { ControllerSettingsStore.Save(_inputSettings); }
            catch (Exception saveError) { ErrorLogStore.Log(saveError.Message, "Performance mode settings", saveError.ToString()); }
            ApplyPerformanceMode(true);
        }
        catch (Exception exception)
        {
            ErrorLogStore.Log(exception.Message, "Performance mode controller selection", exception.ToString());
            ShowNotification("Performance mode selection failed");
        }
    }
    private void UpdatePerformanceModeButtons()
    {
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

    private void ShowButtonGuideToggle_Click(object sender, RoutedEventArgs e)
    {
        _inputSettings.ShowButtonGuide = ShowButtonGuideToggle.IsChecked == true;
        ControllerSettingsStore.Save(_inputSettings);
        UpdateTaskControllerPrompts();
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
        if (IsWinKeyOverlayOpen)
        {
            CloseWinKeyOverlay();
            return;
        }
        OpenWinKeyOverlay();
    }

    internal IReadOnlyList<GameEntry> VisibleGames => _allGames;
    internal string CurrentPerformanceMode => _inputSettings.PerformanceMode ?? "Balanced";
    internal bool IsWinKeyOverlayOpen => _winKeyOverlay?.IsVisible == true;

    internal void OpenWinKeyOverlay()
    {
        CloseAltTabOverlay();
        _winKeyOverlay ??= new WinKeyOverlayWindow(this);
        _winKeyOverlay.OpenOverlay();
    }

    internal void CloseWinKeyOverlay() => _winKeyOverlay?.CloseOverlay();

    internal void ApplyPerformanceModeFromOverlay(string mode)
    {
        if (mode is not ("Ultimate" or "Balanced" or "Eco")) return;
        _inputSettings.PerformanceMode = mode;
        try { ControllerSettingsStore.Save(_inputSettings); }
        catch (Exception saveError) { ErrorLogStore.Log(saveError.Message, "Performance mode settings", saveError.ToString()); }
        ApplyPerformanceMode(true);
    }

    internal void ShowNotificationCenter()
    {
        Show();
        WindowState = WindowState.Maximized;
        Activate();
        Topmost = true;
        Dispatcher.BeginInvoke(() => Topmost = false, DispatcherPriority.ApplicationIdle);
        PopulateNotifications();
        NotificationCenterOverlay.Visibility = Visibility.Visible;
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
        var source = _allGames.Where(game => !game.IsHidden).ToList();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? source
            : source.Where(game => game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   (game.Genres?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                   (game.Developer?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                   game.StoreName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        GamesList.ItemsSource = filtered;
        SetEmptyLibrary(filtered.Count == 0, "NO MATCHING GAMES", "Try a different search term.");
        StatusText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{source.Count} GAMES READY"
            : $"{filtered.Count} SEARCH RESULT{(filtered.Count == 1 ? string.Empty : "S")}";
    }

    private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || GameSearchBox.IsKeyboardFocusWithin) return;
        if (PowerOverlay.Visibility == Visibility.Visible || SettingsOverlay.Visibility == Visibility.Visible ||
            ConnectionsOverlay.Visibility == Visibility.Visible ||
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
        var source = _allGames.Where(game => !game.IsHidden).ToList();
        GamesList.ItemsSource = source;
        SetEmptyLibrary(source.Count == 0, "NO GAMES FOUND", "Refresh the library to scan again.");
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
        if (_controller.IsConnected != _lastControllerConnected)
        {
            if (IsLoaded) ShowNotification(_controller.IsConnected
                ? $"Controller connected: {_controller.ConnectedControllerName}"
                : "Controller disconnected");
            _lastControllerConnected = _controller.IsConnected;
            UpdateTaskControllerPrompts();
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

    private void DismissibleOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (ReferenceEquals(sender, ConnectionsOverlay)) ConnectionsOverlay.Visibility = Visibility.Collapsed;
        else if (ReferenceEquals(sender, ErrorLogOverlay)) CloseErrorLog();
        else if (ReferenceEquals(sender, AccessoriesOverlay)) AccessoriesOverlay.Visibility = Visibility.Collapsed;
        else if (ReferenceEquals(sender, MetadataCorrectionOverlay))
            CancelMetadataCorrection_Click(this, new RoutedEventArgs());
        else if (ReferenceEquals(sender, NotificationCenterOverlay)) NotificationCenterOverlay.Visibility = Visibility.Collapsed;
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
        StatusText.Text = "SCANNING FOR GAMES";
        var progress = new Progress<int>(value =>
        {
            ScanProgress.Value = value;
        });
        var scanStatus = new Progress<string>(message => StatusText.Text = message);

        try
        {
            var knownGames = _allGames.Select(game => game.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var games = await Task.Run(() => GameLibrary.Load(progress));
            var newGames = games.Count(game => !knownGames.Contains(game.Target));
            await MetadataEnrichmentService.EnrichAsync(games, progress, scanStatus);
            DisplayGames(games);
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
        var targetOs = _inputSettings.DefaultDesktop;
        if (string.IsNullOrWhiteSpace(targetOs))
        {
            StatusText.Text = "SCAN AND SELECT A DEFAULT DESKTOP FIRST";
            return;
        }
        var bcdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "bcd-info.txt");
        if (!File.Exists(bcdPath)) { StatusText.Text = "SCAN OS BEFORE SWITCHING DESKTOP"; return; }
        var output = File.ReadAllText(bcdPath);
        var currentDesc = string.Empty;
        var currentMatch = Regex.Match(output, @"(?im)identifier\s+\{current\}.*?^\s*description\s+(.+?)\s*$", RegexOptions.Singleline);
        if (currentMatch.Success) currentDesc = currentMatch.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(currentDesc))
        {
            var fallback = Regex.Match(output, @"(?is)Windows Boot Loader\s+---+\s+identifier\s+\{current\}[^\n]*\n(?:[^\n]*\n)*?\s*description\s+(.+?)\s*$");
            if (fallback.Success) currentDesc = fallback.Groups[1].Value.Trim();
        }
        if (!string.IsNullOrWhiteSpace(currentDesc) &&
            (targetOs.Contains(currentDesc, StringComparison.OrdinalIgnoreCase) ||
             currentDesc.Contains(targetOs, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                AllowShellClose = true;
                Close();
            }
            catch (Exception exception) { StatusText.Text = $"FAILED TO START DESKTOP: {exception.Message}"; }
            return;
        }
        try
        {
            var escaped = Regex.Escape(targetOs);
            var identifier = string.Empty;
            var entries = Regex.Split(output, @"(?=^\s*(?:Windows Boot|Firmware|Resume)[^\n]*\n)", RegexOptions.Multiline);
            foreach (var entry in entries)
            {
                if (!Regex.IsMatch(entry, $@"description\s+[^\r\n]*{escaped}", RegexOptions.IgnoreCase)) continue;
                var idMatch = Regex.Match(entry, @"identifier\s+(\{[^}]+\})", RegexOptions.IgnoreCase);
                if (idMatch.Success) { identifier = idMatch.Groups[1].Value; break; }
            }
            if (string.IsNullOrWhiteSpace(identifier))
            {
                var singleMatch = Regex.Match(output, $@"(?im)(identifier\s+(\{{[^}}]+\}})\s*\r?\n(?:(?!\r?\n)[^\r?\n]*\r?\n)*?\s*description\s+[^\r\n]*{escaped}[^\r\n]*)");
                if (singleMatch.Success) identifier = singleMatch.Groups[2].Value;
            }
            if (string.IsNullOrWhiteSpace(identifier)) { StatusText.Text = "SELECTED OS BOOT ENTRY NOT FOUND"; return; }
            var command = $"bcdedit /set {{fwbootmgr}} bootsequence {identifier} /addfirst && shutdown /r /t 0";
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"Start-Process cmd.exe -Verb RunAs -ArgumentList '/c {command}'\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception exception) { StatusText.Text = $"FAILED TO SWITCH DESKTOP: {exception.Message}"; }
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
        if (e.Key == Key.Escape && ErrorLogOverlay.Visibility == Visibility.Visible)
        {
            CloseErrorLog();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && AccessoriesOverlay.Visibility == Visibility.Visible)
        {
            AccessoriesOverlay.Visibility = Visibility.Collapsed;
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
        try
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
                    {
                        var selectedButton = button;
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                if (!selectedButton.IsEnabled) return;
                                if (selectedButton.Tag is GameEntry game)
                                    Game_Click(selectedButton, new RoutedEventArgs(Button.ClickEvent, selectedButton));
                                else if (selectedButton == ErrorLogButton)
                                    ErrorLog_Click(selectedButton, new RoutedEventArgs(Button.ClickEvent, selectedButton));
                                else if (selectedButton.Tag is string mode && mode is "Ultimate" or "Balanced" or "Eco")
                                    PerformanceMode_Click(selectedButton, new RoutedEventArgs(Button.ClickEvent, selectedButton));
                                else
                                    selectedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            catch (Exception exception)
                            {
                                ErrorLogStore.Log(exception.Message, "Controller selection", exception.ToString());
                                ShowNotification("Controller selection failed");
                            }
                        }, DispatcherPriority.Input);
                    }
                    else if (Keyboard.FocusedElement == BackupStatusHost)
                        OpenBackupSettings();
                    else if (Keyboard.FocusedElement == ContinueHost)
                        ContinueGame_Click(this, new RoutedEventArgs());
                    else if (Keyboard.FocusedElement is Border border && border.Name == "MediaAlbumArt")
                        OpenMediaApp();
                    else if (Keyboard.FocusedElement == MediaPrevBorder)
                        MediaPrev_Click(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
                    else if (Keyboard.FocusedElement == MediaPlayBorder)
                        MediaPlayPause_Click(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
                    else if (Keyboard.FocusedElement == MediaNextBorder)
                        MediaNext_Click(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
                    else if (Keyboard.FocusedElement == OpenSpotifyBorder)
                        OpenSpotify_Click(this, new RoutedEventArgs());
                    else if (Keyboard.FocusedElement is ComboBox comboBox)
                        comboBox.IsDropDownOpen = !comboBox.IsDropDownOpen;
                    break;
                case ControllerCommand.Back:
                    ControllerBack();
                    break;
                case ControllerCommand.PreviousTab:
                    NavigateController(ControllerCommand.Left);
                    break;
                case ControllerCommand.NextTab:
                    NavigateController(ControllerCommand.Right);
                    break;
                case ControllerCommand.PreviousPage:
                case ControllerCommand.NextPage:
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
        catch (Exception exception)
        {
            ErrorLogStore.Log(exception.Message, "Controller selection", exception.ToString());
            ShowNotification("Controller input error logged");
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
            var mediaDeps = new UIElement[] { MediaAlbumArt, MediaPrevBorder, MediaPlayBorder, MediaNextBorder, OpenSpotifyBorder }
                .Where(b => b is not null && b.Visibility == Visibility.Visible).ToList();
            var isOnContinue = focused is not null && (ReferenceEquals(focused, ContinueHost) || IsAncestorOf(ContinueHost, focused));
            var isOnMedia = focused is not null && mediaDeps.Any(b => ReferenceEquals(b, focused) || IsAncestorOf(b, focused));
            var isOnPerf = focused is not null && (ReferenceEquals(focused, PerformanceModeHost) || IsAncestorOf(PerformanceModeHost, focused));
            var isOnNotif = focused is not null && (ReferenceEquals(focused, NotificationsHost) || IsAncestorOf(NotificationsHost, focused));

            if (command is ControllerCommand.Up or ControllerCommand.Down)
            {
                var off = command == ControllerCommand.Down ? 1 : -1;
                var zones = new List<UIElement>();
                if (ContinueHost.Visibility == Visibility.Visible) zones.Add(ContinueHost);
                zones.AddRange(mediaDeps);
                zones.Add(PerformanceModeHost);
                if (NotificationsHost.Visibility == Visibility.Visible) zones.Add(NotificationsHost);
                if (zones.Count == 0) return;
                var ci = -1;
                if (isOnContinue) ci = 0;
                else if (isOnMedia)
                {
                    var mediaIdx = mediaDeps.FindIndex(b => ReferenceEquals(b, focused) || IsAncestorOf(b, focused!));
                    ci = mediaIdx + (ContinueHost.Visibility == Visibility.Visible ? 1 : 0);
                }
                else if (isOnPerf) ci = zones.IndexOf(PerformanceModeHost);
                else if (isOnNotif) ci = zones.Count - 1;
                var ni = ci < 0 ? 0 : Math.Clamp(ci + off, 0, zones.Count - 1);
                var target = zones[ni];
                if (ReferenceEquals(target, ContinueHost)) ContinueHost.Focus();
                else if (target is UIElement u) u.Focus();
                RememberHomeFocus();
                return;
            }
        }
        var activeLibraryList = GamesList;
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
        return null;
    }

    private void RememberHomeFocus()
    {
        if (!_homeSelected || Keyboard.FocusedElement is not DependencyObject focused) return;
        var button = FindAncestor<Button>(focused);
        if (button?.Tag is GameEntry application) _lastHomeSelection = application.Name;
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

    private void CapturePreviousForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != new WindowInteropHelper(this).Handle)
            _previousForegroundWindow = foreground;
    }

    private void RestorePreviousForegroundWindow()
    {
        if (_suppressFocusRestore || _previousForegroundWindow == IntPtr.Zero) return;
        var target = _previousForegroundWindow;
        _previousForegroundWindow = IntPtr.Zero;
        _suppressFocusRestore = false;
        try
        {
            if (IsWindow(target))
            {
                ShowWindow(target, 9);
                BringWindowToTop(target);
                var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                var targetThread = GetWindowThreadProcessId(target, out _);
                var currentThread = GetCurrentThreadId();
                if (foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, true);
                if (targetThread != currentThread) AttachThreadInput(currentThread, targetThread, true);
                SetForegroundWindow(target);
            }
        }
        catch (Exception exception)
        {
            ErrorLogStore.Log(exception.Message, "Restore foreground", exception.ToString());
        }
    }

    private static bool IsTaskSwitcherWindow(IntPtr handle, IntPtr shellHandle)
    {
        // Do not reject a window just because it has an owner. WinUI, Chromium and other
        // modern desktop applications frequently use owned top-level windows for their
        // primary UI (ChatGPT is one example).
        if (handle == shellHandle) return false;
        bool minimized = IsIconic(handle);
        if (!IsWindowVisible(handle) && !minimized) return false;
        // Cloak values: 1 = cloaked by the app (hidden), 2 = cloaked by the shell
        // (UWP/WinUI frames like Settings). Minimized windows also report as cloaked.
        // Only reject genuinely app-cloaked windows that aren't minimized so hidden
        // browser windows and shell-cloaked UWP apps (Settings, Calculator) still show.
        if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 &&
            cloaked == 1 && !minimized) return false;
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

    private void ShowAltTabOverlay()
    {
        if (_altTabActive) return;
        _altTabActive = true;
        var shellHandle = new WindowInteropHelper(this).Handle;
        _altTabWindows = GetTaskWindows(shellHandle).ToList();
        if (_altTabWindows.Count == 0) { _altTabActive = false; return; }

        Topmost = true;
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        _altTabIndex = 0;
        CapturePreviousForegroundWindow();
        _suppressFocusRestore = false;
        AltTabStripList.ItemsSource = null;
        AltTabStripList.ItemsSource = _altTabWindows;
        AltTabOverlay.Visibility = Visibility.Visible;
        UpdateAltTabSelection();
        Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            RegisterAltTabStripThumbnails();
            UpdateAltTabStripHighlight();
        }, DispatcherPriority.Loaded);
    }

    private void CompleteAltTab()
    {
        if (!_altTabActive || _altTabWindows.Count == 0) return;
        var window = _altTabWindows[_altTabIndex];
        ActivateTaskWindow(window);
    }

    private void CycleAltTabSelection()
    {
        if (!_altTabActive || _altTabWindows.Count == 0) return;
        _altTabIndex = (_altTabIndex + 1) % _altTabWindows.Count;
        UpdateAltTabSelection();
    }

    private void UpdateAltTabSelection()
    {
        if (_altTabWindows.Count == 0) return;
        var window = _altTabWindows[_altTabIndex];
        AltTabWindowTitle.Text = window.Title;
        AltTabWindowProcess.Text = window.ProcessName;
        RegisterAltTabThumbnail(window);
        UpdateAltTabStripHighlight();
    }

    private void UpdateAltTabStripHighlight()
    {
        var cards = FindVisualChildren<Border>(AltTabStripList)
            .Where(border => border.Name == "AltTabStripCard").ToList();
        for (var i = 0; i < cards.Count; i++)
        {
            var selected = i == _altTabIndex;
            cards[i].BorderBrush = selected
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF003C"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10FFFFFF"));
            cards[i].BorderThickness = selected ? new Thickness(2) : new Thickness(1.5);
            cards[i].Background = selected
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26FF003C"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10FFFFFF"));
        }
    }

    private void RegisterAltTabStripThumbnails()
    {
        foreach (var thumbnail in _altTabStripThumbnails) DwmUnregisterThumbnail(thumbnail);
        _altTabStripThumbnails.Clear();
        var destination = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var preview in FindVisualChildren<Border>(AltTabStripList)
                     .Where(border => border.Tag is TaskWindowEntry && border.IsVisible))
        {
            var window = (TaskWindowEntry)preview.Tag;
            if (DwmRegisterThumbnail(destination, window.Handle, out var thumbnail) != 0 || thumbnail == IntPtr.Zero)
                continue;
            var properties = new DwmThumbnailProperties
            {
                Flags = 0x1 | 0x4 | 0x8 | 0x10,
                Destination = GetThumbnailRect16To9(preview, dpi.DpiScaleX, dpi.DpiScaleY),
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = false
            };
            DwmUpdateThumbnailProperties(thumbnail, ref properties);
            _altTabStripThumbnails.Add(thumbnail);
        }
    }

    private NativeRect GetThumbnailRect16To9(FrameworkElement target, double dpiScaleX, double dpiScaleY)
    {
        var point = target.TransformToAncestor(this).Transform(new Point(0, 0));
        var w = target.ActualWidth;
        var h = target.ActualHeight;
        const double ratio = 16.0 / 9.0;
        double nw, nh;
        if (w / h > ratio)
        {
            nw = h * ratio;
            nh = h;
        }
        else
        {
            nw = w;
            nh = w / ratio;
        }
        return new NativeRect
        {
            Left = (int)Math.Round((point.X + (w - nw) / 2) * dpiScaleX),
            Top = (int)Math.Round((point.Y + (h - nh) / 2) * dpiScaleY),
            Right = (int)Math.Round((point.X + (w + nw) / 2) * dpiScaleX),
            Bottom = (int)Math.Round((point.Y + (h + nh) / 2) * dpiScaleY)
        };
    }

    private void RegisterAltTabThumbnail(TaskWindowEntry window)
    {
        if (_altTabThumbnail != IntPtr.Zero) DwmUnregisterThumbnail(_altTabThumbnail);
        _altTabThumbnail = IntPtr.Zero;
        var destination = new WindowInteropHelper(this).Handle;
        if (DwmRegisterThumbnail(destination, window.Handle, out var thumbnail) != 0 || thumbnail == IntPtr.Zero)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var point = AltTabPreview.TransformToAncestor(this).Transform(new Point(0, 0));
        var properties = new DwmThumbnailProperties
        {
            Flags = 0x1 | 0x4 | 0x8 | 0x10,
            Destination = new NativeRect
            {
                Left = (int)Math.Round(point.X * dpi.DpiScaleX),
                Top = (int)Math.Round(point.Y * dpi.DpiScaleY),
                Right = (int)Math.Round((point.X + AltTabPreview.ActualWidth) * dpi.DpiScaleX),
                Bottom = (int)Math.Round((point.Y + AltTabPreview.ActualHeight) * dpi.DpiScaleY)
            },
            Opacity = 255,
            Visible = true,
            SourceClientAreaOnly = false
        };
        DwmUpdateThumbnailProperties(thumbnail, ref properties);
        _altTabThumbnail = thumbnail;
    }

    private void CloseAltTabOverlay()
    {
        if (_altTabThumbnail != IntPtr.Zero) DwmUnregisterThumbnail(_altTabThumbnail);
        _altTabThumbnail = IntPtr.Zero;
        foreach (var thumbnail in _altTabStripThumbnails) DwmUnregisterThumbnail(thumbnail);
        _altTabStripThumbnails.Clear();
        AltTabStripList.ItemsSource = null;
        AltTabOverlay.Visibility = Visibility.Collapsed;
        _altTabActive = false;
        Topmost = false;
        RestorePreviousForegroundWindow();
    }

    private void ActivateTaskWindow(TaskWindowEntry window)
    {
        _suppressFocusRestore = true;
        CloseWinKeyOverlay();
        CloseAltTabOverlay();
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
        _suppressFocusRestore = false;
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
        var controllerActive = _controller.IsConnected;
        ControllerGuideBar.Visibility = controllerActive && _inputSettings.ShowButtonGuide ? Visibility.Visible : Visibility.Collapsed;
        if (!controllerActive) return;

        var profile = _inputSettings.Controllers.TryGetValue(_controller.ConnectedControllerName, out var saved)
            ? saved
            : new ControllerProfile();
        var accept = profile.Bindings.GetValueOrDefault(ControllerCommand.Accept, ControllerButton.A);
        var back = profile.Bindings.GetValueOrDefault(ControllerCommand.Back, ControllerButton.B);
        var previousTab = profile.Bindings.GetValueOrDefault(ControllerCommand.PreviousTab, ControllerButton.LeftShoulder);
        var nextTab = profile.Bindings.GetValueOrDefault(ControllerCommand.NextTab, ControllerButton.RightShoulder);
        GuideAcceptGlyph.Text = ControllerButtonGlyph(accept, _controller.ConnectedControllerName);
        GuidePreviousTabGlyph.Text = ControllerButtonGlyph(previousTab, _controller.ConnectedControllerName);
        GuideNextTabGlyph.Text = ControllerButtonGlyph(nextTab, _controller.ConnectedControllerName);
        var backGlyph = ControllerButtonGlyph(back, _controller.ConnectedControllerName);
        GuideBackGlyph.Text = backGlyph;
        ConnectionsBackButton.Content = backGlyph;
        DetailsBackButton.Content = backGlyph;
        ErrorLogBackButton.Content = backGlyph;
        BackupSettingsBackButton.Content = backGlyph;
        PowerBackButton.Content = backGlyph;
        SettingsBackButton.Content = backGlyph;
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

    private void FlashBackButton()
    {
        Button? btn = null;
        if (ConnectionsOverlay.Visibility == Visibility.Visible) btn = ConnectionsBackButton;
        else if (ErrorLogOverlay.Visibility == Visibility.Visible) btn = ErrorLogBackButton;
        else if (BackupSettingsOverlay.Visibility == Visibility.Visible) btn = BackupSettingsBackButton;
        else if (PowerOverlay.Visibility == Visibility.Visible) btn = PowerBackButton;
        else if (SettingsOverlay.Visibility == Visibility.Visible) btn = SettingsBackButton;
        else if (GameDetailsPage.Visibility == Visibility.Visible) btn = DetailsBackButton;
        if (btn is null) return;
        var transform = new ScaleTransform(1.3, 1.3, 0.5, 0.5);
        btn.RenderTransform = transform;
        var animScaleX = new DoubleAnimation(1.3, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var animScaleY = new DoubleAnimation(1.3, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animScaleX);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animScaleY);
    }

    private void ControllerBack()
    {
        FlashBackButton();
        if (AccessoriesOverlay.Visibility == Visibility.Visible) AccessoriesOverlay.Visibility = Visibility.Collapsed;
        else if (ConnectionsOverlay.Visibility == Visibility.Visible) ConnectionsOverlay.Visibility = Visibility.Collapsed;
        else if (ErrorLogOverlay.Visibility == Visibility.Visible) CloseErrorLog();
        else if (MetadataCorrectionOverlay.Visibility == Visibility.Visible)
            CancelMetadataCorrection_Click(this, new RoutedEventArgs());
        else if (BackupSettingsOverlay.Visibility == Visibility.Visible) CloseBackupSettings();
        else if (GameSearchHost.Visibility == Visibility.Visible &&
                 (GameSearchBox.IsKeyboardFocusWithin || !string.IsNullOrWhiteSpace(GameSearchBox.Text))) CloseGameSearch();
        else if (GameDetailsPage.Visibility == Visibility.Visible) CloseGameDetails();
        else if (PowerOverlay.Visibility == Visibility.Visible) ClosePowerOptions();
        else if (SettingsOverlay.Visibility == Visibility.Visible &&
                 (OsSettingsPanel.Visibility == Visibility.Visible ||
                  ControllerSettingsPanel.Visibility == Visibility.Visible ||
                  MetadataSettingsPanel.Visibility == Visibility.Visible ||
                  NetworkSettingsPanel.Visibility == Visibility.Visible ||
                  ControllerCalibrationPanel.Visibility == Visibility.Visible ||
                  AddMetadataSourcePanel.Visibility == Visibility.Visible ||
                  RemoveSourceListPanel.Visibility == Visibility.Visible ||
                  RemoveMetadataSourcePanel.Visibility == Visibility.Visible ||
                  BackgroundSettingsPanel.Visibility == Visibility.Visible))
            BackToSettingsHome_Click(this, new RoutedEventArgs());
        else if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        else if (!_homeSelected) ShowHomePage();
    }

    private static readonly IEasingFunction IosSettle = new QuinticEase { EasingMode = EasingMode.EaseOut };

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform transform) return transform;
        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static async Task PanPageInAsync(FrameworkElement page, double direction, bool animate)
    {
        if (!animate || direction == 0 || page.ActualWidth <= 0) return;
        var transform = EnsureTranslate(page);
        var travel = Math.Max(720, page.ActualWidth);
        transform.X = direction * travel;
        page.IsHitTestVisible = false;
        try
        {
            await AnimateAsync(transform, TranslateTransform.XProperty, transform.X, 0, 450, IosSettle);
        }
        finally
        {
            page.IsHitTestVisible = true;
        }
    }

    private void GameLibraryHeader_Click(object sender, MouseButtonEventArgs e) => ShowHomePage();

    private void OmenBrand_Click(object sender, MouseButtonEventArgs e) => ShowHomePage();

    private void ShowHomePage()
    {
        CloseHomeTaskWindows();
        if (GameDetailsPage.Visibility == Visibility.Visible) CloseGameDetails();
        if (PowerOverlay.Visibility == Visibility.Visible) ClosePowerOptions();
        if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        GameSearchBox.Text = string.Empty;
        _homeSelected = true;
        _homeHoverLocked = false;
        GamesList.IsHitTestVisible = true;
        LibraryScroller.Visibility = Visibility.Visible;
        UpdateContinuePlaying();
        GameLibraryHeader.Foreground = Brushes.White;
        GamesList.ItemsSource = _allGames.Where(game => !game.IsHidden).ToList();
        GameSearchHost.Visibility = Visibility.Visible;
        PerformanceModeHost.Visibility = Visibility.Visible;
        MediaControlsBar.Visibility = Visibility.Visible;
        NotificationsHost.Visibility = Visibility.Visible;
        LibraryFilters.Visibility = Visibility.Visible;
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
            _winKeyOverlay?.ForceClose();
            _batteryOverlay?.ForceClose();
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
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
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

    private sealed class BackgroundChoice
    {
        public BackgroundChoice(string name, ImageSource? thumb, bool isCurrent, string path)
        {
            Name = name;
            Thumb = thumb;
            IsCurrent = isCurrent;
            Path = path;
        }

        public string Name { get; }
        public ImageSource? Thumb { get; }
        public bool IsCurrent { get; }
        public string Path { get; }
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

    private void ListScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer sv)
            UpdateScrollArrow(sv);
    }

    private void UpdateScrollArrow(ScrollViewer sv)
    {
        var arrow = sv.Name switch
        {
            "AccessoriesScroll" => /* AccessoriesScrollArrow */ null,
            "NotificationsScroll" => NotificationsScrollArrow,
            "ClipboardScroll" => ClipboardScrollArrow,
            _ => null
        };
        if (arrow != null)
            arrow.Visibility = sv.VerticalOffset + sv.ViewportHeight < sv.ExtentHeight - 2
                ? Visibility.Visible : Visibility.Collapsed;

        var parent = sv.Parent as Grid;
        if (parent != null)
        {
            var sibling = parent.Children.OfType<TextBlock>()
                .FirstOrDefault(t => t.Name.EndsWith("ScrollArrow"));
            if (sibling != null)
                sibling.Visibility = sv.VerticalOffset + sv.ViewportHeight < sv.ExtentHeight - 2
                    ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void CheckListBoxArrows()
    {
        CheckListBoxArrow(WifiNetworksList, WifiScrollArrow);
        CheckListBoxArrow(BluetoothDevicesList, BluetoothScrollArrow);
    }

    private void CheckListBoxArrow(ListBox lb, TextBlock arrow)
    {
        if (lb == null || arrow == null) return;
        var sv = FindVisualChildren<ScrollViewer>(lb).FirstOrDefault();
        if (sv != null)
            arrow.Visibility = sv.VerticalOffset + sv.ViewportHeight < sv.ExtentHeight - 2
                ? Visibility.Visible : Visibility.Collapsed;
        else
            arrow.Visibility = Visibility.Collapsed;
    }
}


























































