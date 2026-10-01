using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using TvRemote.Configuration;

namespace TvRemote.Host;

// The service binary lives under Program Files; only this owner's configuration is writable.
internal static class PreLoginSetup
{
    internal const string ServiceName = "TvRemoteSignIn";
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TvRemoteSignIn");
    internal static string SettingsDirectory => Path.Combine(DataDirectory, "settings");
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TV Remote Sign-in");
    internal static bool Installed => File.Exists(Path.Combine(DataDirectory, "owner.sid"));
    internal static RemoteConfig ExportConfig(IConfigStore store)
    {
        var config = JsonSerializer.Deserialize<RemoteConfig>(JsonSerializer.Serialize(store.Current, ConfigStore.Json), ConfigStore.Json)!;
        config.AppShortcuts = []; config.EnableHttps = true; config.StartWithWindows = false;
        foreach (var device in config.PairedDevices)
            device.ProtectedTokenHash = Convert.ToBase64String(ProtectedData.Protect(
                ProtectedData.Unprotect(Convert.FromBase64String(device.ProtectedTokenHash), null, store.ProtectionScope), null, DataProtectionScope.LocalMachine));
        return config;
    }
    internal static void SyncIfInstalled(ConfigStore store)
    {
        if (!Installed || !string.Equals(store.DirectoryPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TvRemote"), StringComparison.OrdinalIgnoreCase)) return;
        if (File.ReadAllText(Path.Combine(DataDirectory, "owner.sid")) != WindowsIdentity.GetCurrent().User?.Value) return;
        Export(store, SettingsDirectory);
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
    }
    internal static void Export(ConfigStore store, string destination)
    {
        var certificate = Path.Combine(store.DirectoryPath, "server.dpapi");
        if (!store.Current.EnableHttps || !File.Exists(certificate)) throw new InvalidOperationException("Enable local HTTPS before setting up Windows sign-in.");
        AtomicWrite(Path.Combine(destination, "server.dpapi"), ProtectedData.Protect(ProtectedData.Unprotect(File.ReadAllBytes(certificate), null, store.ProtectionScope), null, DataProtectionScope.LocalMachine));
        AtomicWrite(Path.Combine(destination, "TV-Remote-Root.cer"), File.ReadAllBytes(Path.Combine(store.DirectoryPath, "TV-Remote-Root.cer")));
        // Config is written last; the service detects its timestamp to apply phone revocations.
        AtomicWrite(Path.Combine(destination, "config.json"), JsonSerializer.SerializeToUtf8Bytes(ExportConfig(store), ConfigStore.Json));
    }
    internal static void LaunchInstaller(ConfigStore store)
    {
        if (store.Current.PairedDevices.Count == 0) throw new InvalidOperationException("Pair your Android phone on HTTPS first.");
        var packet = Path.Combine(store.DirectoryPath, "sign-in-setup"); Directory.CreateDirectory(packet);
        Restrict(packet, WindowsIdentity.GetCurrent().User!, false);
        Export(store, packet);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--install-sign-in"); start.ArgumentList.Add(packet);
        start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        Process.Start(start);
    }
    internal static void LaunchRemoval()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--remove-sign-in"); Process.Start(start);
    }
    internal static void Remove()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Administrator rights are required.");
        PreLoginService.StopInstalled(); Sc("delete", ServiceName);
        var firewall = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "advfirewall", "firewall", "delete", "rule", "name=TV Remote Windows Sign-in" }) firewall.ArgumentList.Add(arg);
        using var rule = Process.Start(firewall)!; rule.WaitForExit();
        File.Move(Path.Combine(DataDirectory, "owner.sid"), Path.Combine(DataDirectory, "owner.disabled.sid"), true);
        MessageBox.Show("Windows sign-in service removed. The normal remote and paired phones are unchanged. Its protected installation files have been retained.", "TV Remote");
    }
    private static void Restrict(string path, SecurityIdentifier? owner, bool publicRead)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner ?? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (owner != null) security.AddAccessRule(new(owner, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (publicRead) security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static void RejectReparsePoints(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Sign-in setup cannot use redirected folders or files.");
        foreach (var file in Directory.GetFiles(path)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Sign-in setup cannot use redirected folders or files.");
        foreach (var directory in Directory.GetDirectories(path)) RejectReparsePoints(directory);
    }
    private static void Sc(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows service setup failed: " + output);
    }
    internal static void Install(string packet, string ownerSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Administrator rights are required.");
        var owner = new SecurityIdentifier(ownerSid);
        RejectReparsePoints(packet); RejectReparsePoints(InstallDirectory); RejectReparsePoints(DataDirectory);
        if (Directory.Exists(DataDirectory) && !Installed && !File.Exists(Path.Combine(DataDirectory, "owner.disabled.sid")))
            throw new InvalidOperationException("The sign-in settings folder already exists without a valid installation. No files were changed.");
        if (Directory.Exists(InstallDirectory) && !Installed && !File.Exists(Path.Combine(DataDirectory, "owner.disabled.sid")))
            throw new InvalidOperationException("The sign-in program folder already exists without a valid installation. No files were changed.");
        var config = JsonSerializer.Deserialize<RemoteConfig>(File.ReadAllText(Path.Combine(packet, "config.json")), ConfigStore.Json)!;
        ConfigStore.Validate(config);
        if (!config.EnableHttps || config.PairedDevices.Count == 0 || config.AppShortcuts.Count != 0) throw new InvalidDataException("Pair a phone and enable HTTPS before setup.");
        if (Installed && File.ReadAllText(Path.Combine(DataDirectory, "owner.sid")) != ownerSid) throw new InvalidOperationException("Windows sign-in is already configured by a different Windows user.");
        // Fixed destination, no phone-supplied paths, and no service executing out of a user-writable checkout.
        Directory.CreateDirectory(InstallDirectory); Restrict(InstallDirectory, null, true);
        if (Installed) PreLoginService.StopInstalled();
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(InstallDirectory, Path.GetFileName(file)), true);
        var web = Path.Combine(InstallDirectory, "wwwroot"); Directory.CreateDirectory(web);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) File.Copy(file, Path.Combine(web, Path.GetFileName(file)), true);
        Directory.CreateDirectory(DataDirectory); Restrict(DataDirectory, null, true);
        var stagedSettings = Path.Combine(DataDirectory, "settings-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagedSettings); Restrict(stagedSettings, null, false);
        foreach (var name in new[] { "server.dpapi", "TV-Remote-Root.cer", "config.json" }) File.Copy(Path.Combine(packet, name), Path.Combine(stagedSettings, name), false);
        // Replace the directory itself, without following or writing through user-writable children.
        if (Directory.Exists(SettingsDirectory)) Directory.Move(SettingsDirectory, Path.Combine(DataDirectory, "settings-backup-" + Guid.NewGuid().ToString("N")));
        Directory.Move(stagedSettings, SettingsDirectory); Restrict(SettingsDirectory, owner, false);
        var installed = Installed;
        File.WriteAllText(Path.Combine(DataDirectory, "owner.sid"), ownerSid);
        var binary = "\"" + Path.Combine(InstallDirectory, "TvRemote.Server.exe") + "\" --sign-in-service";
        Sc(installed ? "config" : "create", ServiceName, "binPath=", binary, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "TV Remote Windows Sign-in");
        Sc("description", ServiceName, "HTTPS input for paired phones before Windows sign-in. Stops listening after sign-in.");
        // Per-binary Private/LocalSubnet HTTPS rule, no Internet exposure or UAC policy changes.
        var firewall = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "advfirewall", "firewall", "add", "rule", "name=TV Remote Windows Sign-in", "dir=in", "action=allow", "program=" + Path.Combine(InstallDirectory, "TvRemote.Server.exe"), "protocol=TCP", "localport=" + config.HttpsPort, "profile=private", "remoteip=localsubnet" }) firewall.ArgumentList.Add(arg);
        using var rule = Process.Start(firewall)!; rule.WaitForExit(); if (rule.ExitCode != 0) throw new InvalidOperationException("Could not create the sign-in service's Private-network firewall rule.");
        Sc("start", ServiceName);
        MessageBox.Show("Windows sign-in service installed. It listens on your existing HTTPS address before sign-in and accepts only paired phones. Keep Start with Windows enabled for the normal remote after sign-in.\n\nBoot/sign-in must be tested on this PC; Ctrl+Alt+Delete and BitLocker startup are not supported.", "TV Remote sign-in setup");
    }
}
