using System.Runtime.InteropServices;
using Microsoft.Win32;
using TvRemote.Configuration;

namespace TvRemote.Services;

public sealed record InstalledApp(string Name, string Path);
public static class InstalledAppDiscovery
{
    public static bool IsLocalProgram(string? path) => !string.IsNullOrWhiteSpace(path) &&
        System.IO.Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\") &&
        System.IO.Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static Task<InstalledApp[]> FindAsync()
    {
        var result = new TaskCompletionSource<InstalledApp[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { result.SetResult(Find()); } catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "TV Remote installed apps" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        return result.Task;
    }

    public static InstalledApp? FromFile(string file)
    {
        if (System.IO.Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            object? shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return null;
                shell = Activator.CreateInstance(type);
                return ReadShortcut(shell!, file);
            }
            catch (COMException) { return null; }
            finally { if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }
        return IsLocalProgram(file) ? new(System.IO.Path.GetFileNameWithoutExtension(file), file) : null;
    }

    private static InstalledApp? ReadShortcut(object shell, string file)
    {
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(file);
            string target = ((dynamic)link).TargetPath;
            string arguments = ((dynamic)link).Arguments;
            // Read metadata only. Never run shortcuts, scripts or their arguments.
            return string.IsNullOrWhiteSpace(arguments) && IsLocalProgram(target)
                ? new(System.IO.Path.GetFileNameWithoutExtension(file), target) : null;
        }
        catch (COMException) { return null; }
        finally { if (link != null) Marshal.FinalReleaseComObject(link); }
    }

    private static InstalledApp[] Find()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        void Add(InstalledApp? app) { if (app != null) apps.TryAdd(app.Path, app); }
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null) shell = Activator.CreateInstance(type);
            if (shell != null)
                foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
                {
                    var root = Environment.GetFolderPath(folder);
                    if (!Directory.Exists(root)) continue;
                    foreach (var file in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(2000))
                        Add(ReadShortcut(shell, file));
                }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { }
        finally { if (shell != null) Marshal.FinalReleaseComObject(shell); }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var paths = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                    if (paths == null) continue;
                    foreach (var key in paths.GetSubKeyNames())
                    {
                        using var entry = paths.OpenSubKey(key);
                        if (entry?.GetValue("") is string path) Add(FromFile(Environment.ExpandEnvironmentVariables(path.Trim('"'))));
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        foreach (var app in RemoteConfig.Defaults().Where(a => a.Type == "executable"))
            if (IsLocalProgram(app.Path)) Add(new(app.Name, app.Path!));
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
