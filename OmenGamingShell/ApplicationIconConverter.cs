using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OmenGamingShell;

public sealed class ApplicationIconConverter : IValueConverter
{
    // Icon extraction is expensive (COM + Win32 shell calls). Cache the results so
    // re-realizing the apps grid on every page switch doesn't re-extract every icon
    // on the UI thread, which previously stalled the apps page and froze all other
    // shell animations (overlays, slideshow, cover grid) for the duration.
    private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        var extracted = Extract(path);
        if (extracted is not null) IconCache[path] = extracted;
        return extracted;
    }

    private static ImageSource? Extract(string path)
    {
        if (File.Exists(path) && IsImageFile(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 256;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { }
        }
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var shellIcon = GetHighResolutionIcon(path);
            if (shellIcon is not null) return shellIcon;
        }
        var (iconPath, iconIndex) = ResolveIconLocation(path);
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            var extractedIcon = IntPtr.Zero;
            if (PrivateExtractIcons(iconPath, iconIndex, 256, 256, out extractedIcon, IntPtr.Zero, 1, 0) > 0 &&
                extractedIcon != IntPtr.Zero)
            {
                try { return CreateImage(extractedIcon, 256); }
                finally { DestroyIcon(extractedIcon); }
            }
            var highResolution = GetHighResolutionIcon(iconPath);
            if (highResolution is not null) return highResolution;
            var largeIcons = new IntPtr[1];
            if (ExtractIconEx(iconPath, iconIndex, largeIcons, null, 1) > 0 && largeIcons[0] != IntPtr.Zero)
            {
                try { return CreateImage(largeIcons[0], 64); }
                finally { DestroyIcon(largeIcons[0]); }
            }
        }

        var info = new ShellFileInfo();
        var result = SHGetFileInfo(iconPath ?? path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellGetFileInfoFlags.Icon | ShellGetFileInfoFlags.LargeIcon);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try
        {
            return CreateImage(info.Icon, 64);
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    private static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".ico";

    private static ImageSource? GetHighResolutionIcon(string path)
    {
        var interfaceId = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var factory) != 0 || factory is null)
            return null;
        try
        {
            if (factory.GetImage(new NativeSize { Width = 256, Height = 256 }, 0x1 | 0x4, out var bitmap) != 0 ||
                bitmap == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(bitmap); }
        }
        finally
        {
            if (Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
        }
    }

    private static ImageSource CreateImage(IntPtr icon, int size)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(size, size));
        source.Freeze();
        return source;
    }

    private static (string? Path, int Index) ResolveIconLocation(string shortcutPath)
    {
        if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return (shortcutPath, 0);
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return (shortcutPath, 0);
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath });
            if (shortcut is null) return (shortcutPath, 0);
            var shortcutType = shortcut.GetType();
            var iconLocation = shortcutType.InvokeMember("IconLocation", BindingFlags.GetProperty,
                null, shortcut, null) as string;
            var targetPath = shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty,
                null, shortcut, null) as string;
            if (!string.IsNullOrWhiteSpace(iconLocation))
            {
                var separator = iconLocation.LastIndexOf(',');
                var rawPath = separator >= 0 ? iconLocation[..separator] : iconLocation;
                var rawIndex = separator >= 0 ? iconLocation[(separator + 1)..] : "0";
                var resolved = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
                if (File.Exists(resolved)) return (resolved, int.TryParse(rawIndex, out var index) ? index : 0);
            }
            var target = Environment.ExpandEnvironmentVariables((targetPath ?? string.Empty).Trim().Trim('"'));
            return File.Exists(target) ? (target, 0) : (shortcutPath, 0);
        }
        catch { return (shortcutPath, 0); }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info,
        uint size, ShellGetFileInfoFlags flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int iconIndex, IntPtr[]? largeIcons,
        IntPtr[]? smallIcons, uint iconCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int iconIndex, int iconWidth, int iconHeight,
        out IntPtr icon, IntPtr iconId, uint iconCount, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? imageFactory);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [Flags]
    private enum ShellGetFileInfoFlags : uint
    {
        Icon = 0x000000100,
        LargeIcon = 0x000000000
    }
}
