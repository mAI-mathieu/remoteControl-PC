using System.Diagnostics;
using System.Runtime.InteropServices;
namespace TvRemote.Services;
public interface IPowerService { void Execute(string action); }
public sealed class PowerService : IPowerService
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
    public void Execute(string action)
    {
        switch (action)
        {
            case "lock": if (!LockWorkStation()) throw new InvalidOperationException("Windows could not lock this session."); break;
            case "sleep": if (!SetSuspendState(false, false, false)) throw new InvalidOperationException("Windows could not enter sleep."); break;
            case "restart": case "shutdown":
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { Arguments = action == "restart" ? "/r /t 0" : "/s /t 0", UseShellExecute = false, CreateNoWindow = true }); break;
            default: throw new InvalidOperationException("Unknown power action.");
        }
    }
}
