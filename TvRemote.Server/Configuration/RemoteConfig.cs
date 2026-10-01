using System.Text.Json;
using System.Text.Json.Serialization;

namespace TvRemote.Configuration;

public sealed class RemoteConfig
{
    public int ServerPort { get; set; } = 8123;
    public int HttpsPort { get; set; } = 8124;
    public bool EnableHttps { get; set; }
    public bool StartWithWindows { get; set; }
    public string DeviceName { get; set; } = "Living Room PC";
    public double MouseSensitivity { get; set; } = 1;
    public List<PairedDevice> PairedDevices { get; set; } = [];
    public List<AppShortcut> AppShortcuts { get; set; } = Defaults();

    public static List<AppShortcut> Defaults() =>
    [
        new("playnite", "Playnite", "gamepad", "executable", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite", "Playnite.FullscreenApp.exe")),
        new("steam", "Steam", "gamepad", "executable", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe")),
        new("plex", "Plex", "film", "url", Url: "https://app.plex.tv"),
        new("kodi", "Kodi", "film", "executable", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kodi", "kodi.exe")),
        new("spotify", "Spotify", "music", "url", Url: "https://open.spotify.com"),
        new("youtube", "YouTube", "play", "url", Url: "https://youtube.com"),
        new("netflix", "Netflix", "film", "url", Url: "https://netflix.com"),
        new("browser", "Browser", "globe", "url", Url: "https://www.google.com"),
        new("explorer", "Explorer", "folder", "executable", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
    ];
}

public sealed record AppShortcut(string Id, string Name, string Icon, string Type, string? Path = null, string? Url = null);
public sealed class PairedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Phone";
    public string ProtectedTokenHash { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastConnected { get; set; }
}

public interface IConfigStore { RemoteConfig Current { get; } void Save(); }
public sealed class ConfigStore : IConfigStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string DirectoryPath { get; }
    public string FilePath => System.IO.Path.Combine(DirectoryPath, "config.json");
    public RemoteConfig Current { get; }
    public ConfigStore(string? directory = null)
    {
        DirectoryPath = directory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TvRemote");
        Directory.CreateDirectory(DirectoryPath);
        Current = File.Exists(FilePath) ? JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(FilePath), Json) ?? throw new InvalidDataException("Empty configuration") : new();
        Validate(Current);
        Save();
    }
    public static void Validate(RemoteConfig config)
    {
        if (config.ServerPort is < 1024 or > 65535 || config.HttpsPort is < 1024 or > 65535 || config.HttpsPort == config.ServerPort)
            throw new InvalidDataException("Use distinct HTTP/HTTPS ports between 1024 and 65535.");
        if (string.IsNullOrWhiteSpace(config.DeviceName) || config.DeviceName.Length > 80 || !double.IsFinite(config.MouseSensitivity) || config.MouseSensitivity is < .25 or > 4)
            throw new InvalidDataException("Invalid device name or sensitivity.");
        if (config.PairedDevices is null || config.AppShortcuts is null || config.AppShortcuts.Count > 100 || config.AppShortcuts.Select(a => a.Id).Distinct().Count() != config.AppShortcuts.Count)
            throw new InvalidDataException("Invalid device or shortcut list.");
        foreach (var app in config.AppShortcuts) { if (app == null) throw new InvalidDataException("Invalid shortcut entry."); Services.AppLauncherService.Validate(app); }
        if (config.PairedDevices.Count > 32 || config.PairedDevices.Any(d => d == null || string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.ProtectedTokenHash)))
            throw new InvalidDataException("Invalid paired device data.");
    }
    public void Save()
    {
        lock (Current)
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
            File.Move(temp, FilePath, true);
        }
    }
}
