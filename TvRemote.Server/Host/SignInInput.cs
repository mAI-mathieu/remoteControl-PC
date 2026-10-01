using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TvRemote.Services;

namespace TvRemote.Host;

internal sealed record SignInStroke(ushort Key, bool Up, bool Unicode);
internal sealed record SignInPacket(string Kind, int Dx = 0, int Dy = 0, uint Flags = 0, int Data = 0, SignInStroke[]? Keys = null);

internal sealed class SignInInput : IInputService, IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly object gate = new();
    private readonly Process helper;
    private int disposed;
    internal bool HasExited => helper.HasExited;
    internal SignInInput(uint session)
    {
        var name = "TvRemoteSignIn-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            helper = SignInNative.StartHelper(session, name);
            using var timeout = new CancellationTokenSource(10_000);
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch { pipe.Dispose(); if (helper is not null) { if (!helper.HasExited) helper.Kill(); helper.Dispose(); } throw; }
    }
    private void Send(SignInPacket packet)
    {
        lock (gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, JsonSerializerOptions.Web);
            using var timeout = new CancellationTokenSource(5000);
            try
            {
                pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token).AsTask().GetAwaiter().GetResult();
                pipe.WriteAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
                var result = new byte[1]; pipe.ReadExactlyAsync(result, timeout.Token).AsTask().GetAwaiter().GetResult();
                if (result[0] != 1) throw new InvalidOperationException("Input is available only on the Windows sign-in desktop before a user signs in.");
            }
            catch (OperationCanceledException) { pipe.Dispose(); throw new InvalidOperationException("Windows sign-in input did not respond. Reconnect your phone."); }
        }
    }
    public void Mouse(int dx, int dy, uint flags, int data = 0) => Send(new("mouse", dx, dy, flags, data));
    public void Key(ushort key, bool up = false, bool unicode = false) => BatchKeys([(key, up, unicode)]);
    public void BatchKeys(IEnumerable<(ushort Key, bool Up, bool Unicode)> keys) => Send(new("keys", Keys: keys.Select(k => new SignInStroke(k.Key, k.Up, k.Unicode)).ToArray()));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        pipe.Dispose();
        if (!helper.WaitForExit(2000)) helper.Kill();
        helper.Dispose();
    }
    internal static bool Valid(SignInPacket packet) => packet.Kind switch
    {
        "mouse" => packet.Keys == null && Math.Abs((long)packet.Dx) <= 8000 && Math.Abs((long)packet.Dy) <= 8000 && Math.Abs((long)packet.Data) <= 2400 && packet.Flags is 1 or 2 or 4 or 8 or 16 or 0x800 or 0x1000,
        "keys" => packet.Keys is { Length: > 0 and <= 256 } && packet.Keys.All(k => k != null && (k.Unicode || k.Key is 0x08 or 0x09 or 0x0D or 0x1B or 0x20 or >= 0x21 and <= 0x28 or 0x2E)),
        _ => false
    };
    internal static void RunHelper(string name)
    {
        if (!WindowsIdentity.GetCurrent().IsSystem || !name.StartsWith("TvRemoteSignIn-", StringComparison.Ordinal) || name.Length != 47) throw new UnauthorizedAccessException("Sign-in helper requires the installed Windows service.");
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        client.Connect(10_000);
        // CurrentUserOnly pipe additionally checks both ends have the same SYSTEM identity.
        var input = new InputService();
        var initial = SignInNative.GetThreadDesktop(SignInNative.GetCurrentThreadId());
        try
        {
            while (client.IsConnected)
            {
                var header = new byte[4]; client.ReadExactly(header);
                var length = BitConverter.ToInt32(header); if (length is < 1 or > 32_768) break;
                var bytes = new byte[length]; client.ReadExactly(bytes);
                var packet = JsonSerializer.Deserialize<SignInPacket>(bytes, JsonSerializerOptions.Web);
                var success = false;
                // Button/key releases remain permitted during the handover, preventing stuck input.
                var release = packet?.Kind == "mouse" && packet.Flags is 4 or 16 || packet?.Kind == "keys" && packet.Keys is { Length: > 0 } && packet.Keys.All(k => k.Up);
                if (packet != null && Valid(packet) && (release || !SignInNative.SignedIn(SignInNative.WTSGetActiveConsoleSessionId())))
                {
                    var desktop = SignInNative.OpenInputDesktop(0, false, 0x101); // ReadObjects + SwitchDesktop
                    if (desktop != IntPtr.Zero)
                    {
                        try
                        {
                            var desktopName = new StringBuilder(256);
                            if (SignInNative.GetUserObjectInformation(desktop, 2, desktopName, 512, out _) && (desktopName.ToString() == "Winlogon" || release) && SignInNative.SetThreadDesktop(desktop))
                            {
                                try
                                {
                                    if (packet.Kind == "mouse") input.Mouse(packet.Dx, packet.Dy, packet.Flags, packet.Data);
                                    else input.BatchKeys(packet.Keys!.Select(k => (k.Key, k.Up, k.Unicode)));
                                    success = true;
                                }
                                catch (Win32Exception) { }
                                finally { SignInNative.SetThreadDesktop(initial); }
                            }
                        }
                        finally { SignInNative.CloseDesktop(desktop); }
                    }
                }
                client.WriteByte(success ? (byte)1 : (byte)0);
            }
        }
        catch (IOException) { /* Parent closed its pipe during sign-in/service shutdown. */ }
    }
}

internal static class SignInNative
{
    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int info, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int tokenClass, ref uint information, int length);
    [StructLayout(LayoutKind.Sequential)] private struct Privilege { public uint Count, Low; public int High; public uint Attributes; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref Privilege state, int length, out Privilege previous, out int needed);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)] private static extern bool RestoreTokenPrivileges(IntPtr token, bool disable, ref Privilege state, int length, IntPtr previous, IntPtr needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessInfo process);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup
    {
        public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, XChars, YChars, Fill, Flags; public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);
    internal static bool SignedIn(uint session)
    {
        if (session == uint.MaxValue) return true;
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 5, out var buffer, out _)) return true; // Fail closed.
        try { return !string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer)); } finally { WTSFreeMemory(buffer); }
    }
    internal static Process StartHelper(uint session, string pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem || !DuplicateTokenEx(identity.Token, 0xF01FF, IntPtr.Zero, 2, 1, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var currentToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValue(null, "SeTcbPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var privilege = new Privilege { Count = 1, Low = unchecked((uint)luid), High = (int)(luid >> 32), Attributes = 2 };
                if (!AdjustTokenPrivileges(currentToken, false, ref privilege, Marshal.SizeOf<Privilege>(), out var previous, out _) || Marshal.GetLastWin32Error() == 1300) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { if (!SetTokenInformation(token, 12, ref session, 4)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
                finally { RestoreTokenPrivileges(currentToken, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero); }
            }
            finally { CloseHandle(currentToken); }
            var executable = Environment.ProcessPath!;
            var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Desktop = @"winsta0\Winlogon" };
            var command = new StringBuilder($"\"{executable}\" --sign-in-helper {pipe}");
            if (!CreateProcessAsUser(token, executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Process.GetProcessById((int)process.ProcessId); }
            finally { CloseHandle(process.Process); CloseHandle(process.Thread); }
        }
        finally { CloseHandle(token); }
    }
}
