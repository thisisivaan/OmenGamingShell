using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OmenGamingShell;

public partial class SetupWindow : Window
{
    private const string UltimatePlan = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string EcoPlan = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const int TotalSteps = 7;

    private int _step;
    private string _performanceMode = "Balanced";
    private bool _launched;
    private bool _sourceAdded;
    private InputSettings _inputSettings;

    public SetupWindow()
    {
        InitializeComponent();
        _inputSettings = ControllerSettingsStore.Load();
        SetupInputMethodPicker.ItemsSource = new[] { "Controller", "Mouse & Keyboard" };
        SetupInputMethodPicker.SelectedItem = _inputSettings.InputMethod;
        BuildStepDots();
        ShowStep(0);
    }

    private static List<string> DetectDesktopOptions()
    {
        var options = new List<string> { "Windows" };
        var outputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omen-setup-bcd-" + Guid.NewGuid().ToString("N") + ".txt");        try
        {
            var command = $"Start-Process cmd.exe -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '/c bcdedit /enum all > \"{outputPath}\"'";
            using var scan = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{command}\"")
            {
                UseShellExecute = false, CreateNoWindow = true
            });
            scan?.WaitForExit(20000);
            if (!File.Exists(outputPath)) return options;
            var output = File.ReadAllText(outputPath);
            foreach (var name in new[] { "Ubuntu", "Fedora", "Debian", "Arch", "Linux Mint", "Manjaro", "openSUSE" })
                if (output.Contains(name, StringComparison.OrdinalIgnoreCase) && !options.Contains(name, StringComparer.OrdinalIgnoreCase))
                    options.Add(name);
        }
        catch { }
        finally { try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { } }
        return options;
    }

    private void RefreshDesktopOptions(IEnumerable<string> detected)
    {
        var options = detected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var current = string.IsNullOrWhiteSpace(_inputSettings.DefaultDesktop)
            ? "Windows"
            : _inputSettings.DefaultDesktop;
        if (!options.Contains(current, StringComparer.OrdinalIgnoreCase))
            options.Insert(1, current);
        var selected = SetupDesktopList.SelectedItem as string ?? current;
        SetupDesktopList.ItemsSource = options;
        SetupDesktopList.SelectedItem = options.FirstOrDefault(item =>
            string.Equals(item, selected, StringComparison.OrdinalIgnoreCase)) ?? options[0];
    }

    private void BuildStepDots()
    {
        for (var index = 0; index < TotalSteps; index++)
        {
            SetupStepDots.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(4),
                Fill = new SolidColorBrush(Colors.White)
            });
        }
    }

    private void UpdateStepDots()
    {
        for (var index = 0; index < SetupStepDots.Children.Count; index++)
        {
            if (SetupStepDots.Children[index] is not Ellipse dot) continue;
            dot.Fill = new SolidColorBrush(index < _step
                ? (Color)ColorConverter.ConvertFromString("#66FF003C")
                : index == _step
                    ? (Color)ColorConverter.ConvertFromString("#FF003C")
                    : Colors.White);
        }
    }

    private void ShowStep(int step)
    {
        _step = step;
        WelcomePanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        InputPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PerformancePanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        DesktopPanel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackupPanel.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        MetadataPanel.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;
        FinishPanel.Visibility = step == 6 ? Visibility.Visible : Visibility.Collapsed;

        SetupBackButton.Visibility = step > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupSkipButton.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;
        SetupNextButton.Content = step switch
        {
            0 => "GET STARTED",
            1 => "CONTINUE  →  PERFORMANCE MODE",
            2 => "CONTINUE  →  DEFAULT DESKTOP",
            3 => "CONTINUE  →  BACKUP",
            4 => "CONTINUE  →  COVER ART",
            5 => "REVIEW SUMMARY",
            _ => "LAUNCH SHELL"
        };
        SetupMetadataStatus.Text = string.Empty;
        UpdateStepDots();

        if (step == 2) UpdatePerformanceButtons();
        if (step == 3)
        {
            RefreshDesktopOptions(DetectQuickDesktopOptions());
            SetupDesktopList.Focus();
        }
        if (step == 6) UpdateFinishSummary();
        if (step == 1) SetupInputMethodPicker.Focus();
    }

    private static List<string> DetectQuickDesktopOptions()
    {
        var options = new List<string>();
        var saved = ControllerSettingsStore.Load().DefaultDesktop;
        if (!string.IsNullOrWhiteSpace(saved)) options.Add(saved);
        if (!options.Contains("Windows", StringComparer.OrdinalIgnoreCase)) options.Add("Windows");
        return options;
    }

    private void ScanOs_Click(object sender, RoutedEventArgs e)
    {
        SetupScanOsButton.IsEnabled = false;
        SetupDesktopStatus.Text = "Scanning boot entries — approve the admin prompt to continue...";
        try
        {
            RefreshDesktopOptions(DetectDesktopOptions());
            var count = SetupDesktopList.Items.Count - 1;
            SetupDesktopStatus.Text = count > 0
                ? $"FOUND {count} OTHER DESKTOP{(count == 1 ? "" : "S")}"
                : "ONLY WINDOWS WAS DETECTED";
        }
        finally
        {
            SetupScanOsButton.IsEnabled = true;
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0:
            case 1:
            case 2:
            case 3:
            case 4:
                ShowStep(_step + 1);
                return;
            case 5:
                if (!TrySaveMetadataSource()) return;
                ShowStep(6);
                return;
            case 6:
                Finish();
                return;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 5 && _sourceAdded) RemoveJustSavedSource();
        if (_step > 0) ShowStep(_step - 1);
    }

    private void SkipMetadata_Click(object sender, RoutedEventArgs e)
    {
        ClearMetadataFields();
        ShowStep(6);
    }

    private void Brand_Click(object sender, MouseButtonEventArgs e) => ShowStep(0);

    private void PerformanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        _performanceMode = mode;
        UpdatePerformanceButtons();
    }

    private static Brush ModeBrush(string mode) =>
        CreateFrozenBrush(mode switch
        {
            "Ultimate" => "#C90030",
            "Eco" => "#188A4F",
            _ => "#246BCE"
        });

    private static readonly Brush Glass = CreateFrozenBrush("#18FFFFFF");

    private void UpdatePerformanceButtons()
    {
        SetupUltimateButton.Background = _performanceMode == "Ultimate" ? ModeBrush("Ultimate") : Glass;
        SetupBalancedButton.Background = _performanceMode == "Balanced" ? ModeBrush("Balanced") : Glass;
        SetupEcoButton.Background = _performanceMode == "Eco" ? ModeBrush("Eco") : Glass;
    }

    private bool TrySaveMetadataSource()
    {
        var name = SetupSourceName.Text.Trim();
        var url = SetupSourceUrl.Text.Trim();
        var key = SetupSourceKey.Password;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(key))
        {
            ClearMetadataFields();
            return true;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            SetupMetadataStatus.Text = "Enter a source name (or leave everything blank to skip).";
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var sourceUri) ||
            (sourceUri.Scheme != Uri.UriSchemeHttps && sourceUri.Scheme != Uri.UriSchemeHttp))
        {
            SetupMetadataStatus.Text = "Enter a valid HTTP or HTTPS API URL.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            SetupMetadataStatus.Text = "An API key or client secret is required.";
            return false;
        }
        if (url.Contains("igdb.com", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(SetupSourceClientId.Text.Trim()))
        {
            SetupMetadataStatus.Text = "A Client ID is required for IGDB.";
            return false;
        }

        var settings = MetadataSettingsStore.Load();
        var sourceId = $"custom-{Guid.NewGuid():N}";
        try
        {
            WindowsCredentialStore.SaveMetadataKey(sourceId, key);
            settings.Sources.Add(new MetadataSourceConfig
            {
                Id = sourceId,
                Name = name,
                BaseUrl = url.TrimEnd('/'),
                ClientId = string.IsNullOrWhiteSpace(SetupSourceClientId.Text.Trim())
                    ? null
                    : SetupSourceClientId.Text.Trim()
            });
            settings.PrimarySourceId = sourceId;
            MetadataSettingsStore.Save(settings);
            _sourceAdded = true;
            SetupMetadataStatus.Text = $"{name.ToUpperInvariant()} SOURCE SAVED";
            return true;
        }
        catch (Exception exception)
        {
            SetupMetadataStatus.Text = $"Could not save the source: {exception.Message}";
            return false;
        }
    }

    private void RemoveJustSavedSource()
    {
        if (!_sourceAdded) return;
        var settings = MetadataSettingsStore.Load();
        foreach (var source in settings.Sources)
        {
            if (!source.Id.StartsWith("custom-", StringComparison.OrdinalIgnoreCase)) continue;
            WindowsCredentialStore.DeleteMetadataKey(source.Id);
            settings.Sources.Remove(source);
            break;
        }
        settings.PrimarySourceId = settings.Sources.FirstOrDefault()?.Id ?? string.Empty;
        MetadataSettingsStore.Save(settings);
        _sourceAdded = false;
    }

    private void ClearMetadataFields()
    {
        SetupSourceName.Clear();
        SetupSourceUrl.Text = "https://api.igdb.com/v4";
        SetupSourceClientId.Clear();
        SetupSourceKey.Clear();
        _sourceAdded = false;
    }

    private void UpdateFinishSummary()
    {
        FinishInputText.Text = SetupInputMethodPicker.SelectedItem?.ToString() ?? "Controller";
        FinishPerformanceText.Text = _performanceMode.ToUpperInvariant();
        var desktop = SetupDesktopList.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(desktop)) desktop = _inputSettings.DefaultDesktop;
        FinishDesktopText.Text = string.IsNullOrWhiteSpace(desktop) ? "Windows" : desktop;
        FinishMetadataText.Text = _sourceAdded
            ? SetupSourceName.Text.Trim().ToUpperInvariant()
            : "None configured — you can add one later in Settings";
    }

    private void Finish()
    {
        _inputSettings.InputMethod = SetupInputMethodPicker.SelectedItem as string ?? "Controller";
        _inputSettings.PerformanceMode = _performanceMode;
        if (SetupDesktopList.SelectedItem is string desktop && !string.IsNullOrWhiteSpace(desktop))
            _inputSettings.DefaultDesktop = desktop;
        ControllerSettingsStore.Save(_inputSettings);
        try
        {
            var backupPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "backup-settings.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath, System.Text.Json.JsonSerializer.Serialize(new { folders = Array.Empty<string>(), automatic = SetupAutomaticBackupToggle.IsChecked == true, drive = (string?)null }));
        } catch { }
        ApplyPerformanceMode(_performanceMode);
        SetupStateStore.MarkComplete();
        LaunchShell();
    }

    private void LaunchShell()
    {
        _launched = true;
        var shell = new MainWindow();
        Application.Current.MainWindow = shell;
        shell.Show();
        Close();
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_launched)
        {
            _launched = true;
            var shell = new MainWindow();
            Application.Current.MainWindow = shell;
            shell.Show();
        }
        base.OnClosing(e);
    }

    private static void ApplyPerformanceMode(string mode)
    {
        var scheme = mode switch
        {
            "Ultimate" => UltimatePlan,
            "Eco" => EcoPlan,
            _ => BalancedPlan
        };
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powercfg.exe", $"/setactive {scheme}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}











