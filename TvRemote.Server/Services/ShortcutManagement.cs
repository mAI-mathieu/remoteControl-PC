using TvRemote.Configuration;

namespace TvRemote.Services;

public static class ShortcutManagement
{
    public static AppShortcut Create(string name, string icon, string type, string target, string? id = null)
    {
        target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (type == "url" && !target.Contains(":") && target.Length > 0) target = "https://" + target;
        var app = new AppShortcut(id ?? "app-" + Guid.NewGuid().ToString("N"), name.Trim(), icon, type,
            type == "executable" ? target : null, type == "url" ? target : null);
        AppLauncherService.Validate(app);
        return app;
    }

    public static void Save(IConfigStore store, IEnumerable<AppShortcut> shortcuts)
    {
        var updated = shortcuts.ToList();
        if (updated.Count > 100) throw new InvalidDataException("You can add up to 100 shortcuts.");
        if (updated.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != updated.Count)
            throw new InvalidDataException("Each shortcut must have a different ID.");
        foreach (var app in updated) AppLauncherService.Validate(app);
        lock (store.Current)
        {
            var previous = store.Current.AppShortcuts;
            store.Current.AppShortcuts = updated;
            try { store.Save(); }
            catch { store.Current.AppShortcuts = previous; throw; }
        }
    }
}
