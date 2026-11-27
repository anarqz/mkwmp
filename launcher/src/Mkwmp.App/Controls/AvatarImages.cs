// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mkwmp.App.Controls;

/// <summary>
/// The pictures of <see cref="Avatar"/>: a file (a JPEG or PNG someone else made) decoded off the UI thread into a
/// small frozen bitmap, once per file and size. A file that is not a picture, is too large or cannot be read is no
/// picture: the disc keeps its initials. The file's name changes when the picture does (the core's cache), so a
/// decoded one never goes stale.
/// </summary>
internal static class AvatarImages
{
    /// <summary>The decoded width for the discs of a list and of the top bar.</summary>
    public const int Small = 128;

    /// <summary>For the large disc of an account card.</summary>
    public const int Large = 256;

    /// <summary>A file larger than this is not decoded (the core keeps none above 2 MB).</summary>
    private const long MaxFile = 2 * 1024 * 1024;

    /// <summary>A picture wider or taller than this is not decoded: a small file can claim a huge canvas.</summary>
    private const int MaxSide = 4096;

    /// <summary>The decoded pictures kept (a friends list is 300 at most; 128 px is 64 kB each).</summary>
    private const int Most = 400;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string Path, int Pixels), Task<BitmapSource?>> Loads = [];
    private static readonly LinkedList<(string Path, int Pixels)> Order = new();

    /// <summary>The picture when it was decoded already; null otherwise (also for a file that is no picture).</summary>
    public static BitmapSource? Cached(string path, int pixels)
    {
        lock (Gate)
        {
            return Loads.TryGetValue((path, pixels), out var load) && load.IsCompletedSuccessfully ? load.Result : null;
        }
    }

    /// <summary>The picture, decoded on another thread (once: a second ask waits for the first); null when the file is not one.</summary>
    public static Task<BitmapSource?> LoadAsync(string path, int pixels)
    {
        lock (Gate)
        {
            var key = (path, pixels);
            if (Loads.TryGetValue(key, out var load))
            {
                return load;
            }
            Order.AddLast(key);
            while (Order.Count > Most && Order.First is { } oldest)
            {
                Loads.Remove(oldest.Value);
                Order.RemoveFirst();
            }
            return Loads[key] = Task.Run(() => Decode(path, pixels));
        }
    }

    /// <summary>Reads the whole file first (it may be replaced meanwhile), then decodes it scaled down: never more than <see cref="Large"/> wide in memory.</summary>
    internal static BitmapSource? Decode(string path, int pixels)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxFile)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            using (var probe = new MemoryStream(bytes, writable: false))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                if (frame.PixelWidth is < 1 or > MaxSide || frame.PixelHeight is < 1 or > MaxSide)
                {
                    return null;
                }
                pixels = Math.Min(pixels, Math.Max(frame.PixelWidth, 16)); // never blown up
            }
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = pixels;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException
            or OverflowException or FileFormatException or System.Runtime.InteropServices.COMException or OutOfMemoryException)
        {
            return null; // not a picture WPF reads: the initials
        }
    }

    /// <summary>
    /// The disc behind the initials from Nextendo's colour ("#rrggbb"); null for anything else. A light colour is
    /// darkened: the initials on it are white.
    /// </summary>
    public static Brush? Disc(string? color)
    {
        if (color is not { Length: 7 } || color[0] != '#'
            || !int.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return null;
        }
        var (r, g, b) = ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        var light = ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255;
        const double Lightest = 0.55;
        if (light > Lightest)
        {
            var k = Lightest / light;
            (r, g, b) = ((int)(r * k), (int)(g * k), (int)(b * k));
        }
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b));
        brush.Freeze();
        return brush;
    }
}
