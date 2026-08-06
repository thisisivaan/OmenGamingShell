using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace OmenGamingShell;

public sealed class StoreIcon : FrameworkElement
{
    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Brush IconBrush = CreateIconBrush();

    public static readonly DependencyProperty StoreNameProperty = DependencyProperty.Register(
        nameof(StoreName), typeof(string), typeof(StoreIcon),
        new FrameworkPropertyMetadata("Local / Windows", FrameworkPropertyMetadataOptions.AffectsRender));

    public string StoreName
    {
        get => (string)GetValue(StoreNameProperty);
        set => SetValue(StoreNameProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var geometry = GetGeometry(StoreName);
        if (geometry is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = geometry.Bounds;
        var scale = Math.Min(ActualWidth / bounds.Width, ActualHeight / bounds.Height);
        drawingContext.PushTransform(new TranslateTransform(
            (ActualWidth - bounds.Width * scale) / 2 - bounds.X * scale,
            (ActualHeight - bounds.Height * scale) / 2 - bounds.Y * scale));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(IconBrush, null, geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static Geometry? GetGeometry(string? storeName)
    {
        var asset = storeName switch
        {
            "Steam" => "steam",
            "Epic Games" => "epicgames",
            "Xbox" => "xbox",
            "GOG" => "gogdotcom",
            "Ubisoft Connect" => "ubisoft",
            "EA app" => "ea",
            "Battle.net" => "battledotnet",
            "Riot Games" => "riotgames",
            "Rockstar Games" => "rockstargames",
            _ => "windows11"
        };
        if (Cache.TryGetValue(asset, out var cached)) return cached;
        try
        {
            var resource = Application.GetResourceStream(
                new Uri($"pack://application:,,,/Assets/Stores/{asset}.svg"));
            if (resource is null) return null;
            using var reader = new StreamReader(resource.Stream);
            var svg = reader.ReadToEnd();
            var match = Regex.Match(svg, "<path\\s+d=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var geometry = Geometry.Parse(match.Groups[1].Value);
            geometry.Freeze();
            Cache[asset] = geometry;
            return geometry;
        }
        catch { return null; }
    }

    private static Brush CreateIconBrush()
    {
        var brush = new SolidColorBrush(Colors.White);
        brush.Freeze();
        return brush;
    }
}
