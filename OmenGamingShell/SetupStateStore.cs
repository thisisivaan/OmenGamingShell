using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public static class SetupStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string PathName => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "setup-state.json");

    public static bool IsSetupComplete()
    {
        try
        {
            if (!File.Exists(PathName)) return false;
            var state = JsonSerializer.Deserialize<SetupState>(File.ReadAllText(PathName), Options);
            return state?.IsComplete == true;
        }
        catch { return false; }
    }

    public static void MarkComplete()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, JsonSerializer.Serialize(new SetupState { IsComplete = true }, Options));
    }

    private sealed class SetupState
    {
        public bool IsComplete { get; set; }
    }
}