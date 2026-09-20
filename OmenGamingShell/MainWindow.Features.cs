using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OmenGamingShell;

public partial class MainWindow
{
    private DispatcherTimer _mediaPollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _earphoneTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _volumeBrightnessTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private int _lastVolumePct = -1;
    private int _lastBrightnessPct = -1;
    private DateTime _lastBrightnessPollUtc = DateTime.MinValue;
    private bool _volumeDragging;
    private bool _brightnessDragging;
    private VolumeBrightnessOsd? _osd;
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
        // Media controls - event-driven (instant updates)
        MediaService.Start(async () => await Dispatcher.BeginInvoke(async () => await UpdateMediaControlsAsync()));

        // Safety-net poll so browser/chromium media (which can be slow to fire SMTC
        // events) still appears without delay.
        _mediaPollTimer.Tick += async (_, _) => await UpdateMediaControlsAsync();
        _mediaPollTimer.Start();

        // Volume & brightness - live bars + Windows-style OSD on any change
        InitVolumeBrightness();

        // App catalog used by the WinKey overlay search - load cached copy eagerly
        // so the first search is instant, then refresh from Windows in the background.
        InstalledAppsCatalog.WarmUp();

        // Earphone/audio endpoint status (slow poll - PnP query is expensive)
        _earphoneTimer.Tick += (_, _) => CheckEarphoneStatus();
        _earphoneTimer.Start();

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
            _ = Dispatcher.BeginInvoke(() => ShowOnboarding(), DispatcherPriority.ApplicationIdle);
        }
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
            _ = Dispatcher.BeginInvoke(() =>
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
                }
            });
        }
        catch
        {
            _ = Dispatcher.BeginInvoke(() =>
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

    private static async void AnimateButtonFlash(Border border)
    {
        var original = border.Background;
        border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF003C"));
        await Task.Delay(200);
        border.Background = original;
    }

    // === NOTIFICATION CENTER ===
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
        if (count > 0) NotificationsHost.Visibility = Visibility.Visible;
        NotificationsHostEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationsHostList.ItemsSource = NotificationCenter.Items;
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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            var icon = new TextBlock { Text = n.Icon, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A0A0A0")), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0);
            var stack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            stack.Children.Add(new TextBlock { Text = n.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) });
            stack.Children.Add(new TextBlock { Text = n.Message, FontSize = 11, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#909090")), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            stack.Children.Add(new TextBlock { Text = n.Time.ToString("HH:mm"), FontSize = 9, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#606060")), Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(icon);
            grid.Children.Add(stack);
            var dismiss = new Button
            {
                Content = "\uE8BB",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 10,
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#606060")),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Dismiss"
            };
            dismiss.Click += (_, _) =>
            {
                if (string.Equals(n.Tag, UpdateNotificationTag, StringComparison.Ordinal))
                    _updateDismissedThisSession = true;
                NotificationCenter.Remove(n);
                PopulateNotifications();
            };
            Grid.SetColumn(dismiss, 2);
            grid.Children.Add(dismiss);
            item.Child = grid;
            if (string.Equals(n.Tag, UpdateNotificationTag, StringComparison.Ordinal))
            {
                item.Cursor = Cursors.Hand;
                item.MouseLeftButtonUp += (_, _) => OpenUpdateFromNotificationAsync();
            }
            NotificationsList.Children.Add(item);
        }
        NotificationsEmpty.Visibility = NotificationCenter.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationCenter.HasTag(UpdateNotificationTag)) _updateDismissedThisSession = true;
        NotificationCenter.Clear();
        PopulateNotifications();
    }

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

    private void InitVolumeBrightness()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var vol = (int)Math.Round(SystemAudio.GetVolume());
                _lastVolumePct = vol;
                UpdateVolumeUI(vol);
            }
            catch { }
        });
        _ = RefreshBrightnessAsync();
        _volumeBrightnessTimer.Tick += async (_, _) => await PollVolumeBrightnessAsync();
        _volumeBrightnessTimer.Start();
    }

    private Task PollVolumeBrightnessAsync()
    {
        try
        {
            if (!_volumeDragging)
            {
                var vol = (int)Math.Round(SystemAudio.GetVolume());
                if (_lastVolumePct < 0) _lastVolumePct = vol;
                if (vol != _lastVolumePct)
                {
                    _lastVolumePct = vol;
                    UpdateVolumeUI(vol);
                    ShowOsd(vol, false);
                }
            }
        }
        catch { }

        // Brightness WMI reads are expensive - throttle to ~1.5s.
        if ((DateTime.UtcNow - _lastBrightnessPollUtc).TotalMilliseconds >= 1500 && !_brightnessDragging)
        {
            _lastBrightnessPollUtc = DateTime.UtcNow;
            _ = RefreshBrightnessAsync();
        }
        return Task.CompletedTask;
    }

    private static int? ReadBrightness()
    {
        try
        {
            var cmd = "Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness | Select-Object -ExpandProperty CurrentBrightness";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", PowerShellCommand(cmd))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim() ?? null;
            proc?.WaitForExit(3000);
            if (int.TryParse(output, out var bright)) return bright;
        }
        catch { }
        return null;
    }

    private async Task RefreshBrightnessAsync()
    {
        var bright = await Task.Run(ReadBrightness);
        if (bright is null) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_lastBrightnessPct < 0) _lastBrightnessPct = bright.Value;
            if (bright.Value != _lastBrightnessPct)
            {
                _lastBrightnessPct = bright.Value;
                UpdateBrightnessUI(bright.Value);
                ShowOsd(bright.Value, true);
            }
        });
    }

    private static void SetSystemBrightness(int percent)
    {
        try
        {
            var cmd = "Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods | Invoke-CimMethod -MethodName WmiSetBrightness -Argument @{Brightness=" + percent + "; Timeout=1}";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", PowerShellCommand(cmd))
            { UseShellExecute = false, CreateNoWindow = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private void UpdateVolumeUI(int pct)
    {
        var track = VolumeBarTrack.ActualWidth;
        if (track <= 0) track = 140;
        var fill = track * Math.Clamp(pct, 0, 100) / 100.0;
        VolumeBarFill.Width = fill;
        VolumeThumb.Margin = new Thickness(Math.Clamp(fill - 6.5, 0, track - 13), 0, 0, 0);
        VolumeText.Text = pct.ToString();
    }

    private void UpdateBrightnessUI(int pct)
    {
        var track = BrightnessBarTrack.ActualWidth;
        if (track <= 0) track = 140;
        var fill = track * Math.Clamp(pct, 0, 100) / 100.0;
        BrightnessBarFill.Width = fill;
        BrightnessThumb.Margin = new Thickness(Math.Clamp(fill - 6.5, 0, track - 13), 0, 0, 0);
        BrightnessText.Text = pct.ToString();
    }

    private void ShowOsd(int pct, bool isBrightness)
    {
        if (_osd is null)
        {
            _osd = new VolumeBrightnessOsd();
            _osd.Closed += (_, _) => _osd = null;
        }
        _osd.ShowOsd(pct, isBrightness);
    }

    public void ShowVolumeOsd(int pct)
    {
        _ = Dispatcher.BeginInvoke(() => ShowOsd(pct, false));
    }

    // --- Volume slider drag ---
    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _volumeDragging = true;
        VolumeRow.CaptureMouse();
        VolumeSetFromPosition(e.GetPosition(VolumeBarTrack));
        e.Handled = true;
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_volumeDragging && e.LeftButton == MouseButtonState.Pressed)
            VolumeSetFromPosition(e.GetPosition(VolumeBarTrack));
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_volumeDragging) return;
        _volumeDragging = false;
        VolumeRow.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void VolumeSetFromPosition(Point pos)
    {
        var track = VolumeBarTrack.ActualWidth;
        if (track <= 0) return;
        var pct = (int)Math.Round(Math.Clamp(pos.X / track, 0, 1) * 100);
        if (pct == _lastVolumePct)
        {
            UpdateVolumeUI(pct);
            return;
        }
        SystemAudio.SetVolume(pct);
        _lastVolumePct = pct;
        UpdateVolumeUI(pct);
        ShowOsd(pct, false);
    }

private void BrightnessBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _brightnessDragging = true;
        BrightnessRow.CaptureMouse();
        BrightnessSetFromPosition(e.GetPosition(BrightnessBarTrack));
        e.Handled = true;
    }

    private void BrightnessBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_brightnessDragging && e.LeftButton == MouseButtonState.Pressed)
            BrightnessSetFromPosition(e.GetPosition(BrightnessBarTrack));
    }

    private void BrightnessBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_brightnessDragging) return;
        _brightnessDragging = false;
        BrightnessRow.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void BrightnessSetFromPosition(Point pos)
    {
        var track = BrightnessBarTrack.ActualWidth;
        if (track <= 0) return;
        var pct = (int)Math.Round(Math.Clamp(pos.X / track, 0, 1) * 100);
        if (pct == _lastBrightnessPct)
        {
            UpdateBrightnessUI(pct);
            return;
        }
        SetSystemBrightness(pct);
        _lastBrightnessPct = pct;
        UpdateBrightnessUI(pct);
        ShowOsd(pct, true);
    }

    private static string PowerShellCommand(string script)
    {
        return "-NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
    }

    // --- Controller Status Button ---
    private void UpdateControllerIndicator()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            var connected = _controller.IsConnected;
            ControllerStatusBtn.Background = connected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0x00, 0xCC, 0x40))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0x00, 0x3C));
            ControllerStatusText.Text = connected ? "Connected" : "Disconnected";
            ControllerStatusText.Foreground = connected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xCC, 0x40))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
        });
    }

    private void ControllerStatus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // Open the Connections overlay on the Bluetooth tab
        _ = OpenConnectionsAsync(false);
    }


    // --- Audio Device Status ---
    private bool _lastEarphoneConnected;

    private void EarphoneStatus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = OpenConnectionsAsync(false);
    }

    private void CheckEarphoneStatus()
    {
        _ = CheckEarphoneStatusAsync();
    }

    private async Task CheckEarphoneStatusAsync()
    {
        try
        {
            _earphoneTimer.Stop();
            var cmd = "Get-PnpDevice -PresentOnly -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'headphone|headset|earphone|earbud|airpods|buds|earb|handsfree|headset earspeaker|bluetooth.*audio' } | Select-Object -First 1 -ExpandProperty Status";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", PowerShellCommand(cmd))
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return;
            var output = (await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(true))?.Trim() ?? "";
            if (!proc.WaitForExit(5000)) proc.Kill();
            var connected = output == "OK";
            if (connected != _lastEarphoneConnected)
            {
                var wasConnected = _lastEarphoneConnected;
                _lastEarphoneConnected = connected;
                if (connected)
                {
                    NotificationCenter.RemoveTag(AudioNotificationTag);
                    Notify("Audio device connected", "Audio device connected", "Headset or audio device detected", IconAudio, AudioNotificationTag);
                }
                else if (wasConnected)
                {
                    NotificationCenter.RemoveTag(AudioNotificationTag);
                    Notify("Audio device disconnected", "Audio device disconnected", "Headset or audio device removed", IconAudio, AudioNotificationTag);
                }
                EarphoneStatusBtn.Background = connected
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0x00, 0xCC, 0x40))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0x00, 0x3C));
                EarphoneStatusText.Text = connected ? "Connected" : "Disconnected";
                EarphoneStatusText.Foreground = connected
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xCC, 0x40))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60));
            }
        }
        catch { }
        finally
        {
            _earphoneTimer.Start();
        }
    }

}
