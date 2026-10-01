using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using TvRemote.Configuration;
using TvRemote.Models;
using TvRemote.Services;

namespace TvRemote.Host;

internal static class PreLoginService
{
    internal static bool Allowed(RemoteCommand command) => command.Type switch
    {
        "mouse_move" or "mouse_click" or "mouse_down" or "mouse_up" or "scroll" or "text" or "text_edit" or "release" or "ping" => true,
        "key" => (command.Modifiers?.Length ?? 0) == 0 && command.Key is "ENTER" or "BACKSPACE" or "DELETE" or "ESCAPE" or "TAB" or "LEFT" or "RIGHT" or "UP" or "DOWN" or "HOME" or "END" or "PAGEUP" or "PAGEDOWN" or "SPACE",
        _ => false
    };
    private static readonly CancellationTokenSource stop = new();
    private static IntPtr statusHandle;
    private static readonly ServiceMain main = ServiceStarted;
    private static readonly ServiceControl control = Control;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMain(uint count, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceControl(uint command);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry { public string? Name; public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepts, Win32Exit, ServiceExit, Checkpoint, Wait; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher(Entry[] entries);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandler(string name, ServiceControl callback);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(IntPtr service, uint command, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out Status status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    internal static void StopInstalled()
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, PreLoginSetup.ServiceName, 0x24);
            if (service == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    if (!QueryServiceStatus(service, out var state)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if (state.State == 1) return;
                    Thread.Sleep(500);
                }
                throw new TimeoutException("Sign-in service did not stop. No program files were replaced.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    internal static void Run()
    {
        if (!WindowsIdentity.GetCurrent().IsSystem || !string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), PreLoginSetup.InstallDirectory, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Run the sign-in service from its administrator-installed Program Files location.");
        if (!StartServiceCtrlDispatcher([new() { Name = PreLoginSetup.ServiceName, Main = main }, new()]))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void Report(uint state)
    {
        var value = new Status { Type = 0x10, State = state, Accepts = state == 4 ? 5u : 0u, Wait = state is 2 or 3 ? 15_000u : 0u };
        SetServiceStatus(statusHandle, ref value);
    }
    private static void Control(uint command) { if (command is 1 or 5) { Report(3); stop.Cancel(); } }
    private static void ServiceStarted(uint count, IntPtr args)
    {
        statusHandle = RegisterServiceCtrlHandler(PreLoginSetup.ServiceName, control);
        if (statusHandle == IntPtr.Zero) return;
        Report(4);
        try { Supervise().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log(ex); }
        finally { Report(1); }
    }
    private static void Log(Exception ex)
    {
        // Do not write exceptions containing field values, tokens or configuration contents.
        try
        {
            var path = Path.Combine(PreLoginSetup.DataDirectory, "service.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".1", true);
            var nativeCode = ex is System.ComponentModel.Win32Exception native ? $" ({native.NativeErrorCode})" : "";
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} Sign-in service: {ex.GetType().Name}{nativeCode}\n");
        }
        catch (IOException) { }
    }
    private static async Task Supervise()
    {
        ServerRuntime? server = null; SignInInput? input = null;
        uint activeSession = uint.MaxValue; DateTime configTime = default; string? activeNetwork = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var session = SignInNative.WTSGetActiveConsoleSessionId();
                var signedIn = SignInNative.SignedIn(session);
                var timestamp = File.GetLastWriteTimeUtc(Path.Combine(PreLoginSetup.SettingsDirectory, "config.json"));
                var network = string.Join(";", new DiscoveryService().Addresses().Select(a => a.ToString()).Order(StringComparer.Ordinal));
                var helperExited = input?.HasExited == true;
                if (server != null && (signedIn || session != activeSession || timestamp != configTime || helperExited || network != activeNetwork))
                {
                    await server.DisposeAsync(); server = null; input?.Dispose(); input = null;
                }
                if (!signedIn && server == null)
                {
                    try
                    {
                        var store = new ConfigStore(PreLoginSetup.SettingsDirectory, DataProtectionScope.LocalMachine, loginOnly: true);
                        if (!store.Current.EnableHttps || store.Current.AppShortcuts.Count != 0) throw new InvalidDataException("Invalid sign-in settings.");
                        input = new SignInInput(session);
                        server = new(store, new DiscoveryService());
                        var bridge = input;
                        await server.StartAsync(overrides: services =>
                        {
                            services.AddSingleton<IInputService>(bridge);
                            services.AddSingleton<ITextFocusService>(new SignInFocus());
                        });
                        configTime = timestamp; activeSession = session; activeNetwork = network;
                    }
                    catch (Exception ex)
                    {
                        Log(ex);
                        if (server != null) { await server.DisposeAsync(); server = null; }
                        input?.Dispose(); input = null;
                        await Task.Delay(5000, stop.Token);
                    }
                }
                await Task.Delay(1000, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { if (server != null) await server.DisposeAsync(); input?.Dispose(); }
    }
    private sealed class SignInFocus : ITextFocusService
    {
        private long revision = 1;
        public TextFocusState Current => new(true, true, Interlocked.Read(ref revision));
        public event Action<TextFocusState>? Changed;
        // No protected field contents are inspected. Treat a click/navigation as a new masked field.
        public void RequestRefresh() { Interlocked.Increment(ref revision); Changed?.Invoke(Current); }
    }
}
