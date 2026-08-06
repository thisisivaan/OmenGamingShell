using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OmenGamingShell;

public static class ArtworkCache
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    public static string? GetExecutableIcon(string executable)
    {
        if (!File.Exists(executable)) return null;
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenGamingShell", "Metadata");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())));
        var cachePath = Path.Combine(cacheDirectory, $"{key}.png");
        if (File.Exists(cachePath)) return cachePath;

        IntPtr iconHandle = IntPtr.Zero;
        try
        {
            var result = SHGetFileInfo(executable, 0, out var info, (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiLargeIcon);
            iconHandle = info.IconHandle;
            if (result == IntPtr.Zero || iconHandle == IntPtr.Zero) return null;

            var image = Imaging.CreateBitmapSourceFromHIcon(iconHandle, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(256, 256));
            image.Freeze();
            Directory.CreateDirectory(cacheDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(cachePath);
            encoder.Save(output);
            return cachePath;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
        }
    }

    public static string? NormalizeCover(string sourcePath, string cacheKey)
    {
        if (!File.Exists(sourcePath)) return null;
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenGamingShell", "Metadata", "Covers");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey.ToUpperInvariant())));
        var cachePath = Path.Combine(cacheDirectory, $"{key}_600x800.png");
        if (File.Exists(cachePath)) return cachePath;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(sourcePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            const int targetWidth = 600;
            const int targetHeight = 800;
            const double targetRatio = (double)targetWidth / targetHeight;
            var sourceRatio = (double)image.PixelWidth / image.PixelHeight;
            double drawWidth;
            double drawHeight;
            double offsetX;
            double offsetY;
            if (sourceRatio > targetRatio)
            {
                drawHeight = targetHeight;
                drawWidth = targetHeight * sourceRatio;
                offsetX = (targetWidth - drawWidth) / 2;
                offsetY = 0;
            }
            else
            {
                drawWidth = targetWidth;
                drawHeight = targetWidth / sourceRatio;
                offsetX = 0;
                offsetY = (targetHeight - drawHeight) / 2;
            }

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawImage(image, new System.Windows.Rect(offsetX, offsetY, drawWidth, drawHeight));
            var rendered = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();

            Directory.CreateDirectory(cacheDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var output = File.Create(cachePath);
            encoder.Save(output);
            return cachePath;
        }
        catch
        {
            return null;
        }
    }

    public static string? NormalizeBackground(string sourcePath, string cacheKey)
    {
        if (!File.Exists(sourcePath)) return null;
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenGamingShell", "Metadata", "Backgrounds");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey.ToUpperInvariant())));
        var cachePath = Path.Combine(cacheDirectory, $"{key}_2560x1440.jpg");
        if (File.Exists(cachePath)) return cachePath;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(sourcePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            const int targetWidth = 2560;
            const int targetHeight = 1440;
            const double targetRatio = (double)targetWidth / targetHeight;
            var sourceRatio = (double)image.PixelWidth / image.PixelHeight;
            double drawWidth;
            double drawHeight;
            double offsetX;
            double offsetY;
            if (sourceRatio > targetRatio)
            {
                drawHeight = targetHeight;
                drawWidth = targetHeight * sourceRatio;
                offsetX = (targetWidth - drawWidth) / 2;
                offsetY = 0;
            }
            else
            {
                drawWidth = targetWidth;
                drawHeight = targetWidth / sourceRatio;
                offsetX = 0;
                offsetY = (targetHeight - drawHeight) / 2;
            }

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawImage(image, new System.Windows.Rect(offsetX, offsetY, drawWidth, drawHeight));
            var rendered = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();

            Directory.CreateDirectory(cacheDirectory);
            var encoder = new JpegBitmapEncoder { QualityLevel = 96 };
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var output = File.Create(cachePath);
            encoder.Save(output);
            return cachePath;
        }
        catch
        {
            return null;
        }
    }

    public static string? CreateBackgroundFromCover(string sourcePath, string cacheKey)
    {
        if (!File.Exists(sourcePath)) return null;
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenGamingShell", "Metadata", "Backgrounds");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey.ToUpperInvariant())));
        var cachePath = Path.Combine(cacheDirectory, $"{key}_cover_2560x1440.jpg");
        if (File.Exists(cachePath)) return cachePath;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(sourcePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            // Render small first and scale it back up to create a fast, GPU-friendly
            // soft-focus fallback without shipping an image-processing dependency.
            const int smallWidth = 160;
            const int smallHeight = 90;
            var sourceRatio = (double)image.PixelWidth / image.PixelHeight;
            var targetRatio = (double)smallWidth / smallHeight;
            var drawWidth = sourceRatio > targetRatio ? smallHeight * sourceRatio : smallWidth;
            var drawHeight = sourceRatio > targetRatio ? smallHeight : smallWidth / sourceRatio;
            var smallVisual = new DrawingVisual();
            using (var context = smallVisual.RenderOpen())
                context.DrawImage(image, new System.Windows.Rect(
                    (smallWidth - drawWidth) / 2, (smallHeight - drawHeight) / 2, drawWidth, drawHeight));
            var small = new RenderTargetBitmap(smallWidth, smallHeight, 96, 96, PixelFormats.Pbgra32);
            small.Render(smallVisual);
            small.Freeze();

            var finalVisual = new DrawingVisual();
            using (var context = finalVisual.RenderOpen())
            {
                context.DrawImage(small, new System.Windows.Rect(0, 0, 2560, 1440));
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)), null,
                    new System.Windows.Rect(0, 0, 2560, 1440));
            }
            var rendered = new RenderTargetBitmap(2560, 1440, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(finalVisual);
            rendered.Freeze();

            Directory.CreateDirectory(cacheDirectory);
            var encoder = new JpegBitmapEncoder { QualityLevel = 94 };
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var output = File.Create(cachePath);
            encoder.Save(output);
            return cachePath;
        }
        catch { return null; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShFileInfo fileInfo,
        uint fileInfoSize, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
}
