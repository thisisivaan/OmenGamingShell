using System.IO;
using System.Diagnostics;

namespace OmenGamingShell;

public sealed record ScreenshotEntry(string FilePath, DateTime Taken, string FileName);

public static class ScreenshotService
{
    private static readonly string[] CapturePaths = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Camera Roll"),
        @"C:\Users\Public\Pictures\Screenshots",
        @"C:\Users\Public\Videos\Captures"
    };

    public static List<ScreenshotEntry> GetRecentScreenshots(int maxCount = 20)
    {
        var files = new List<ScreenshotEntry>();
        foreach (var dir in CapturePaths)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var ext in new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.mp4", "*.mkv", "*.avi" })
                {
                    foreach (var file in Directory.GetFiles(dir, ext))
                    {
                        var info = new FileInfo(file);
                        files.Add(new ScreenshotEntry(file, info.LastWriteTime, info.Name));
                    }
                }
            }
            catch { }
        }
        return files.OrderByDescending(f => f.Taken).Take(maxCount).ToList();
    }

    public static void OpenScreenshotsFolder()
    {
        var folder = CapturePaths.FirstOrDefault(Directory.Exists) ?? CapturePaths[0];
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }

    public static void OpenFile(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
