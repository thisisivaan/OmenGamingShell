using System.IO;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class MainWindow
{
    private DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DispatcherTimer _mediaPollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _onboardingStep;
    private readonly string[][] _onboardingSteps = new[]
    {
        new[] { "\uE80F", "WELCOME TO OMEN SHELL", "Your personal gaming hub. Let's walk you through everything so you can get the most out of it." },
        new[] { "\uE74C", "GAME LIBRARY", "Your games appear as cover art cards. Hover to preview, click to launch. Use the filters at the bottom to sort by All, Favorites, Recently Played, or by Store. Right-click for more options." },
        new[] { "\uE71D", "APPLICATIONS", "The app grid shows your installed apps — browsers, communication tools, streaming services, and more. Click any icon to launch it instantly." },
        new[] { "\uE767", "MEDIA CONTROLS", "The media bar shows what's currently playing. Use the prev/play/next buttons to control playback. When nothing is playing, click OPEN SPOTIFY to start listening." },
        new[] { "\uE768", "VOLUME & BRIGHTNESS", "Quick sliders for volume and brightness sit right below the media controls. Drag to adjust instantly." },
        new[] { "\uE7FC", "CONTROLLER & AUDIO", "The Accessories container shows your controller and earphone status. A green dot means connected, red means disconnected. Click to open Bluetooth settings." },
        new[] { "\uE765", "WIN KEY OVERLAY", "Press the Windows key to open the launcher overlay. Search for any app, see open windows as live thumbnails, and click to switch." },
        new[] { "\uE74A", "NOTIFICATIONS", "The bell icon shows your notification history. You also have clipboard history, screenshots, and more from the status bar." },
        new[] { "\uE73E", "YOU'RE ALL SET!", "Press the Windows key anytime to search, use your controller to navigate, and explore the settings for performance modes, backup, and customization." }
    };

    private void InitFeatures()
    {
        // Performance monitor timer
        _perfTimer.Tick += (_, _) => UpdatePerfStats();
        _perfTimer.Start();

        // Media controls - event-driven (instant updates)
        MediaService.Start(async () => await Dispatcher.BeginInvoke(async () => await UpdateMediaControlsAsync()));

        // Safety-net poll so browser/chromium media (which can be slow to fire SMTC
        // events) still appears without delay.
        _mediaPollTimer.Tick += async (_, _) => await UpdateMediaControlsAsync();
        _mediaPollTimer.Start();

        // Clipboard history
        ClipboardHistory.Start(Dispatcher);

        // Notification center
        NotificationCenter.Load();
        NotificationCenter.Updated += () => Dispatcher.BeginInvoke(RefreshNotificationsContainer);
        RefreshNotificationsContainer();

        // Onboarding - show on first run
        if (!File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenGamingShell", ".onboarded")))
        {
            Dispatcher.BeginInvoke(() => ShowOnboarding(), DispatcherPriority.ApplicationIdle);
        }
    }

    // === PERFORMANCE MONITOR ===
    private void UpdatePerfStats()
    {
        try
        {
            var stats = PerformanceMonitor.Sample();

            if (PerfDetailsOverlay.Visibility == Visibility.Visible)
            {
                PerfCpuBar.Width = Math.Min(310, 310 * stats.CpuUsage / 100.0);
                PerfRamBar.Width = Math.Min(310, 310 * stats.RamUsagePercent / 100.0);
                PerfUpdatedText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            }
        }
        catch { }
    }

    private void PerfStatsBadge_Click(object sender, MouseButtonEventArgs e)
    {
        UpdatePerfStats();
        PerfDetailsOverlay.Visibility = Visibility.Visible;
    }

    private void ClosePerfOverlay(object sender, RoutedEventArgs e)
    {
        PerfDetailsOverlay.Visibility = Visibility.Collapsed;
    }

    // === MEDIA CONTROLS ===
    private async Task UpdateMediaControlsAsync()
    {
        // Don't let media updates fight the game-cover hover state: hovering a cover
        // hides these containers and shows the game's background art, and a media
        // refresh would re-show the bar and wipe the background.
        if (_gameCoverActive) return;
        try
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
            Dispatcher.BeginInvoke(() =>
            {
                if (media is not null)
                {
                    MediaControlsBar.Visibility = Visibility.Visible;
                    MediaTitle.Text = media.Title;
                    MediaArtist.Text = media.Artist;
                    MediaPlayIcon.Text = media.IsPlaying ? "\uEDB4" : "\uEDB8";
                    OpenSpotifyBorder.Visibility = Visibility.Collapsed;
                    MediaPlayBorder.Visibility = Visibility.Visible;
                    MediaPrevBorder.Visibility = Visibility.Visible;
                    MediaNextBorder.Visibility = Visibility.Visible;
                    if (albumArt is not null)
                    {
                        MediaAlbumArt.Source = albumArt;
                        MediaBgBlur.Source = albumArt;
                        MediaBgBlur.Opacity = media.IsPlaying ? 0.45 : 0;
                        if (FallbackGameBackground.Opacity > 0) HideGameBackground();
                    }
                    else
                    {
                        MediaAlbumArt.Source = null;
                        MediaBgBlur.Source = null;
                        MediaBgBlur.Opacity = 0;
                    }
                    if (media.Duration.TotalSeconds > 0)
                    {
                        var pct = Math.Min(1.0, media.Position.TotalSeconds / media.Duration.TotalSeconds);
                        MediaProgressBar.Width = pct * 170;
                    }
                    UpdateDashMedia(media, albumArt);
                }
                else
                {
                    MediaControlsBar.Visibility = Visibility.Visible;
                    MediaTitle.Text = "No media playing";
                    MediaArtist.Text = "";
                    MediaProgressBar.Width = 0;
                    MediaAlbumArt.Source = null;
                    MediaBgBlur.Source = null;
                    MediaBgBlur.Opacity = 0;
                    OpenSpotifyBorder.Visibility = Visibility.Visible;
                    MediaPlayBorder.Visibility = Visibility.Collapsed;
                    MediaPrevBorder.Visibility = Visibility.Collapsed;
                    MediaNextBorder.Visibility = Visibility.Collapsed;
                    UpdateDashMedia(null, null);
                }
            });
        }
        catch
        {
            Dispatcher.BeginInvoke(() =>
            {
                MediaControlsBar.Visibility = Visibility.Visible;
                MediaTitle.Text = "No media playing";
                MediaArtist.Text = "";
                MediaProgressBar.Width = 0;
                MediaAlbumArt.Source = null;
                MediaBgBlur.Source = null;
                MediaBgBlur.Opacity = 0;
                OpenSpotifyBorder.Visibility = Visibility.Visible;
                MediaPlayBorder.Visibility = Visibility.Collapsed;
                MediaPrevBorder.Visibility = Visibility.Collapsed;
                MediaNextBorder.Visibility = Visibility.Collapsed;
            });
        }
    }
    private async void MediaPlayPause_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateButtonFlash(MediaPlayBorder);
        await MediaService.TogglePlayPauseAsync();
    }

    private async void MediaNext_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateButtonFlash(MediaNextBorder);
        await MediaService.NextAsync();
    }

    private async void MediaPrev_Click(object sender, MouseButtonEventArgs e)
    {
        AnimateButtonFlash(MediaPrevBorder);
        await MediaService.PreviousAsync();
    }

    private void UpdateDashMedia(MediaInfo? media, BitmapImage? albumArt)
    {
        if (WinKeyDashboard is null) return;
        DashMediaTitle.Text = media is null ? "No media playing" : media.Title;
        DashMediaArtist.Text = media is null ? "" : (media.Artist ?? "");
        DashMediaPlayButton.Content = media is { IsPlaying: true } ? "PAUSE" : "PLAY";
        DashMediaPlayButton.Visibility = media is null ? Visibility.Collapsed : Visibility.Visible;
        if (albumArt is not null) DashMediaArt.Background = new ImageBrush(albumArt);
        else DashMediaArt.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10FFFFFF"));
    }

    private async void DashMediaPlay_Click(object sender, RoutedEventArgs e)
    {
        await MediaService.TogglePlayPauseAsync();
    }

    private static void AnimateButtonFlash(Border border)
    {
        var original = border.Background;
        border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF003C"));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { border.Background = original; timer.Stop(); };
        timer.Start();
    }

    // === NOTIFICATION CENTER ===
    private void NotificationCenterBell_Click(object sender, MouseButtonEventArgs e)
    {
        PopulateNotifications();
        NotificationCenterOverlay.Visibility = Visibility.Visible;
    }

    private void NotificationsHost_Click(object sender, MouseButtonEventArgs e)
    {
        PopulateNotifications();
        NotificationCenterOverlay.Visibility = Visibility.Visible;
    }

    private void NotificationsHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            PopulateNotifications();
            NotificationCenterOverlay.Visibility = Visibility.Visible;
        }
    }

    private void RefreshNotificationsContainer()
    {
        var count = NotificationCenter.Items.Count;
        NotificationsHost.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationsHostEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DashNotificationTitle is null) return;
        var latest = NotificationCenter.Items.FirstOrDefault();
        if (latest is not null)
        {
            DashNotificationTitle.Text = latest.Title;
            DashNotificationMessage.Text = latest.Message;
        }
        else
        {
            DashNotificationTitle.Text = "No notifications";
            DashNotificationMessage.Text = string.Empty;
        }
    }

    private void PopulateNotifications()
    {
        NotificationsList.Children.Clear();
        foreach (var n in NotificationCenter.Items)
        {
            var item = new Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14FFFFFF")), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 6) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new TextBlock { Text = n.Icon, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A0A0A0")), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0);
            var stack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            stack.Children.Add(new TextBlock { Text = n.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) });
            stack.Children.Add(new TextBlock { Text = n.Message, FontSize = 11, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#909090")), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            stack.Children.Add(new TextBlock { Text = n.Time.ToString("HH:mm"), FontSize = 9, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#606060")), Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(icon);
            grid.Children.Add(stack);
            item.Child = grid;
            NotificationsList.Children.Add(item);
        }
        NotificationsEmpty.Visibility = NotificationCenter.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseNotificationCenter(object sender, RoutedEventArgs e) => NotificationCenterOverlay.Visibility = Visibility.Collapsed;
    private void ClearNotifications_Click(object sender, RoutedEventArgs e) { NotificationCenter.Clear(); PopulateNotifications(); }

    // === CLIPBOARD HISTORY ===
    private void OpenClipboardHistory()
    {
        ClipboardList.Children.Clear();
        foreach (var c in ClipboardHistory.Items)
        {
            var display = c.Text.Length > 120 ? c.Text.Substring(0, 120) + "..." : c.Text;
            var item = new Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14FFFFFF")), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand };
            item.MouseLeftButtonDown += (_, _) => { Clipboard.SetText(c.Text); };
            var text = new TextBlock { Text = display, FontSize = 11, Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
            var time = new TextBlock { Text = c.Time.ToString("HH:mm:ss"), FontSize = 9, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#606060")), Margin = new Thickness(0, 4, 0, 0) };
            var sp = new StackPanel();
            sp.Children.Add(text);
            sp.Children.Add(time);
            item.Child = sp;
            ClipboardList.Children.Add(item);
        }
        ClipboardEmpty.Visibility = ClipboardHistory.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipboardOverlay.Visibility = Visibility.Visible;
    }

    private void CloseClipboardOverlay(object sender, RoutedEventArgs e) => ClipboardOverlay.Visibility = Visibility.Collapsed;

    // === SCREENSHOTS ===
    private void OpenScreenshotsOverlay()
    {
        ScreenshotsPanel.Children.Clear();
        var shots = ScreenshotService.GetRecentScreenshots();
        foreach (var s in shots)
        {
            var card = new Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#14FFFFFF")), CornerRadius = new CornerRadius(8), Width = 140, Height = 110, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ClipToBounds = true };
            card.MouseLeftButtonDown += (_, _) => ScreenshotService.OpenFile(s.FilePath);
            var sp = new StackPanel { Margin = new Thickness(8) };
            var icon = new TextBlock { Text = s.FileName.EndsWith(".mp4") || s.FileName.EndsWith(".mkv") ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 28, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#707070")), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 6) };
            var name = new TextBlock { Text = s.FileName, FontSize = 10, Foreground = new SolidColorBrush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
            var date = new TextBlock { Text = s.Taken.ToString("MMM dd HH:mm"), FontSize = 9, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#606060")), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            sp.Children.Add(icon); sp.Children.Add(name); sp.Children.Add(date);
            card.Child = sp;
            ScreenshotsPanel.Children.Add(card);
        }
        ScreenshotsEmpty.Visibility = shots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsOverlay.Visibility = Visibility.Visible;
    }
    private void CloseScreenshotsOverlay(object sender, RoutedEventArgs e) => ScreenshotsOverlay.Visibility = Visibility.Collapsed;
    private void OpenScreenshotsFolder_Click(object sender, RoutedEventArgs e) => ScreenshotService.OpenScreenshotsFolder();

    // === ONBOARDING ===
    private void ShowOnboarding()
    {
        _onboardingStep = 0;
        OnboardingDots.Items.Clear();
        for (int i = 0; i < _onboardingSteps.Length; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new System.Windows.Thickness(4, 0, 4, 0),
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0x30, 0x30))
            };
            OnboardingDots.Items.Add(dot);
        }
        UpdateOnboardingStep();
        OnboardingOverlay.Visibility = Visibility.Visible;
    }
    private void UpdateOnboardingStep()
    {
        if (_onboardingStep >= _onboardingSteps.Length) { CompleteOnboarding(); return; }
        OnboardingStep.Text = "Step " + (_onboardingStep + 1) + " of " + _onboardingSteps.Length;
        OnboardingIcon.Text = _onboardingSteps[_onboardingStep][0];
        OnboardingTitle.Text = _onboardingSteps[_onboardingStep][1];
        OnboardingDesc.Text = _onboardingSteps[_onboardingStep][2];
        // Progress bar
        var barMaxWidth = 400.0;
        OnboardingProgressBar.Width = barMaxWidth * (_onboardingStep + 1) / _onboardingSteps.Length;
        // Button text
        var isLast = _onboardingStep == _onboardingSteps.Length - 1;
        var isFirst = _onboardingStep == 0;
        OnboardingNextBtn.Content = isFirst ? "GET STARTED" : isLast ? "FINISH" : "CONTINUE";
        OnboardingSkipBtn.Visibility = isLast ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        // Update progress dots
        for (int i = 0; i < OnboardingDots.Items.Count; i++)
        {
            var dot = (System.Windows.Shapes.Ellipse)OnboardingDots.Items[i];
            dot.Fill = i == _onboardingStep
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x3C))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0x30, 0x30));
            dot.Width = i == _onboardingStep ? 16 : 8;
        }
    }
    private void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        _onboardingStep++;
        if (_onboardingStep >= _onboardingSteps.Length) CompleteOnboarding();
        else UpdateOnboardingStep();
    }
    private void OnboardingSkip_Click(object sender, RoutedEventArgs e) => CompleteOnboarding();
    private void CompleteOnboarding()
    {
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        try
        {
            Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell"));
            File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", ".onboarded"), DateTime.Now.ToString("O"));
        }
        catch { }
    }

    private void OpenSpotify_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var uri = new Uri("spotify:");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://open.spotify.com") { UseShellExecute = true }); } catch { }
        }
    }

    private void ContinueHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            ContinueGame_Click(sender, e);
        }
    }

    private void ContinueHost_FocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
    }

    private void AlbumArt_Click(object sender, MouseButtonEventArgs e) => OpenMediaApp();

    private void AlbumArt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            OpenMediaApp();
        }
    }

    private void MediaButton_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        var fake = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left);
        if (sender == MediaPrevBorder) MediaPrev_Click(sender, fake);
        else if (sender == MediaPlayBorder) MediaPlayPause_Click(sender, fake);
        else if (sender == MediaNextBorder) MediaNext_Click(sender, fake);
        else if (sender == OpenSpotifyBorder) OpenSpotify_Click(sender, new RoutedEventArgs());
    }

    private async void OpenMediaApp()
    {
        try
        {
            // Await (not block) so GetCurrentMediaAsync never deadlocks on the UI context.
            var info = await MediaService.GetCurrentMediaAsync();
            if (info is null) { OpenSpotify_Click(this, new RoutedEventArgs()); return; }
            var appId = info.AppName;
            if (!string.IsNullOrEmpty(appId))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"shell:AppsFolder\\{appId}") { UseShellExecute = true });
            else
                OpenSpotify_Click(this, new RoutedEventArgs());
        }
        catch { OpenSpotify_Click(this, new RoutedEventArgs()); }
    }

    // --- Volume & Brightness ---
    private float _currentVolume = 1.0f;

    private void InitVolumeBrightness()
    {
        try { UpdateVolumeDisplay(); } catch { }
        try { UpdateBrightnessDisplay(); } catch { }
    }

    private void UpdateVolumeDisplay()
    {
        try
        {
            var cmd = "Get-AudioDevice -PlaybackVolume 2>$null";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command " + Quote(cmd))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim() ?? "100";
            proc?.WaitForExit(3000);
            if (float.TryParse(output, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var vol))
            {
                var pct = Math.Clamp(vol > 1 ? vol : vol * 100, 0, 100);
                _currentVolume = pct / 100f;
                Dispatcher.BeginInvoke(() =>
                {
                    VolumeBarFill.Width = 48.0 * pct / 100.0;
                    VolumeText.Text = ((int)pct).ToString();
                });
            }
        }
        catch { }
    }

    private void VolumeBar_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var bar = (FrameworkElement)sender;
            var pos = e.GetPosition(bar);
            var pct = Math.Clamp(pos.X / bar.ActualWidth, 0, 1);
            _currentVolume = (float)pct;
            VolumeBarFill.Width = 48.0 * pct;
            VolumeText.Text = ((int)(pct * 100)).ToString();
        }
        catch { }
    }

    private void UpdateBrightnessDisplay()
    {
        try
        {
            var cmd = "Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness | Select-Object -ExpandProperty CurrentBrightness";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command " + Quote(cmd))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim() ?? "100";
            proc?.WaitForExit(3000);
            if (int.TryParse(output, out var bright))
            {
                Dispatcher.BeginInvoke(() => { BrightnessBarFill.Width = 48.0 * bright / 100.0;
                    BrightnessText.Text = bright.ToString();
                });
            }
        }
        catch { }
    }

    private void BrightnessBar_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var bar = (FrameworkElement)sender;
            var pos = e.GetPosition(bar);
            var pct = Math.Clamp(pos.X / bar.ActualWidth, 0, 1);
            var bright = (int)(pct * 100);
            BrightnessBarFill.Width = 48.0 * pct;
            BrightnessText.Text = bright.ToString();
            var cmd = "Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods | Invoke-CimMethod -MethodName WmiSetBrightness -Argument @{Brightness=" + bright + "; Timeout=1}";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command " + Quote(cmd))
            { UseShellExecute = false, CreateNoWindow = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

        private static string Quote(string s)
    {
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    // --- Controller Status Button ---
    private void UpdateControllerIndicator()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var connected = _controller.IsConnected;
            ControllerIndicatorDot.Fill = connected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xCC, 0x40))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x3C));
            ControllerStatusBtn.Background = connected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0x00, 0xCC, 0x40))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0x00, 0x3C));
        });
    }

    private void ControllerStatus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // Open the Connections overlay on the Bluetooth tab
        _ = OpenConnectionsAsync(false);
    }


    // --- Earphone Status ---
    private bool _lastEarphoneConnected;

    private void EarphoneStatus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = OpenConnectionsAsync(false);
    }

    private void CheckEarphoneStatus()
    {
        try
        {
            var cmd = "Get-PnpDevice -PresentOnly | Where-Object {$_.Class -eq 'AudioEndpoint' -or $_.FriendlyName -match 'headphone|headset|earphone|earbud|airpods|buds'} | Select-Object -First 1 -ExpandProperty Status";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command " + Quote(cmd))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim() ?? "";
            proc?.WaitForExit(5000);
            var connected = output == "OK";
            if (connected != _lastEarphoneConnected)
            {
                _lastEarphoneConnected = connected;
                Dispatcher.BeginInvoke(() =>
                {
                    EarphoneIndicatorDot.Fill = connected
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xCC, 0x40))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x3C));
                    EarphoneStatusBtn.Background = connected
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0x00, 0xCC, 0x40))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0x00, 0x3C));
                    EarphoneStatusText.Text = connected ? "Connected" : "Disconnected";
                    EarphoneStatusText.Foreground = connected
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xCC, 0x40))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
                });
            }
        }
        catch { }
    }

}
