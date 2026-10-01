using System.Collections.Concurrent;
using System.Drawing.Imaging;

namespace TvRemote.Services;

public static class ProgramIcons
{
    private sealed record CachedIcon(DateTime Modified, long Length, byte[]? Png);
    private static readonly ConcurrentDictionary<string, CachedIcon> cache = new(StringComparer.OrdinalIgnoreCase);
    public static byte[]? GetPng(string? path)
    {
        if (!InstalledAppDiscovery.IsLocalProgram(path)) return null;
        try
        {
            var file = new FileInfo(path!);
            if (cache.TryGetValue(path!, out var saved) && saved.Modified == file.LastWriteTimeUtc && saved.Length == file.Length) return saved.Png;
            byte[]? png = null;
            using var icon = Icon.ExtractAssociatedIcon(path!);
            if (icon != null)
            {
                using var bitmap = icon.ToBitmap(); using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png); png = stream.ToArray();
            }
            if (cache.Count >= 256) cache.Clear();
            cache[path!] = new(file.LastWriteTimeUtc, file.Length, png);
            return png;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        { return null; }
    }
    public static Bitmap? GetBitmap(string? path)
    {
        var png = GetPng(path); if (png == null) return null;
        using var stream = new MemoryStream(png); using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
}
