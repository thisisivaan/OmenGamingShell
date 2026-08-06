using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public sealed class ControllerProfile
{
    public int DeadZonePercent { get; set; } = 25;
    public bool VibrationEnabled { get; set; } = true;
    public Dictionary<ControllerCommand, ControllerButton> Bindings { get; set; } = new()
    {
        [ControllerCommand.Accept] = ControllerButton.A,
        [ControllerCommand.Back] = ControllerButton.B,
        [ControllerCommand.PreviousTab] = ControllerButton.LeftShoulder,
        [ControllerCommand.NextTab] = ControllerButton.RightShoulder,
        [ControllerCommand.Settings] = ControllerButton.Menu,
        [ControllerCommand.Power] = ControllerButton.View
    };
}

public sealed class InputSettings
{
    public string InputMethod { get; set; } = "Controller";
    public bool GuideOpensHomeWhileGaming { get; set; } = true;
    public bool NavigationAudioEnabled { get; set; } = true;
    public int NavigationAudioVolume { get; set; } = 35;
    public string PerformanceMode { get; set; } = "Balanced";
    public Dictionary<string, ControllerProfile> Controllers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ControllerSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string PathName => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "controller-settings.json");

    public static InputSettings Load()
    {
        try
        {
            return File.Exists(PathName)
                ? JsonSerializer.Deserialize<InputSettings>(File.ReadAllText(PathName), Options) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void Save(InputSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, JsonSerializer.Serialize(settings, Options));
    }
}

public enum ControllerButton { A, B, X, Y, LeftShoulder, RightShoulder, Menu, View }
