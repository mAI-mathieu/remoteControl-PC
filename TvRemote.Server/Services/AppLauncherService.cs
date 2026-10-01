using System.Diagnostics;
using TvRemote.Configuration;

namespace TvRemote.Services;
public interface IAppLauncherService { object[] List(); void Launch(string id); void ClosePlaynite(); }
public sealed class AppLauncherService(IConfigStore store, ILogger<AppLauncherService> logger) : IAppLauncherService
{
    public static void Validate(AppShortcut app)
    {
        if (string.IsNullOrWhiteSpace(app.Id) || app.Id.Length > 60 || !app.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || string.IsNullOrWhiteSpace(app.Name) || app.Name.Length > 60)
            throw new InvalidDataException("Shortcut needs a unique simple ID and name.");
        if (app.Type == "url")
        {
            if (!Uri.TryCreate(app.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException($"Shortcut {app.Id}: only HTTP(S) URLs are allowed.");
        }
        else if (app.Type != "executable" || string.IsNullOrWhiteSpace(app.Path) || !Path.IsPathFullyQualified(app.Path) || app.Path.StartsWith(@"\\") || !string.Equals(Path.GetExtension(app.Path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Shortcut {app.Id}: use a local absolute .exe path.");
    }
    private string? Resolve(AppShortcut app)
    {
        if (app.Type == "url") return app.Url;
        if (File.Exists(app.Path)) return app.Path;
        if (app.Id == "playnite")
            return new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Playnite", "Playnite.FullscreenApp.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite", "Playnite.FullscreenApp.exe") }.FirstOrDefault(File.Exists);
        return null;
    }
    public object[] List() => store.Current.AppShortcuts.Select(a => (object)new { a.Id, a.Name, a.Icon, available = Resolve(a) != null }).ToArray();
    public void Launch(string id)
    {
        var app = store.Current.AppShortcuts.FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("Unknown shortcut.");
        Validate(app);
        var target = Resolve(app) ?? throw new InvalidOperationException($"{app.Name} is not installed. Edit its path in the host configuration.");
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = app.Type == "executable" ? Path.GetDirectoryName(target) : "" });
        logger.LogInformation("Launched shortcut {Id}", id);
    }
    public void ClosePlaynite()
    {
        var app = store.Current.AppShortcuts.FirstOrDefault(a => a.Id == "playnite") ?? throw new InvalidOperationException("Playnite is not configured.");
        var path = Resolve(app) ?? throw new InvalidOperationException("Playnite is not installed.");
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
                if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) process.CloseMainWindow();
        }
    }
}
