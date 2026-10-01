using TvRemote.Models;
using TvRemote.Services;

namespace TvRemote.WebSockets;
public sealed class CommandDispatcher(IMouseService mouse, IKeyboardService keyboard,
    IVolumeService volume, IAppLauncherService apps, IPowerService power, TvRemote.Configuration.IConfigStore? store = null) : IDisposable
{
    private readonly object gate = new();
    private string? owner;
    private readonly HashSet<string> buttons = [];
    private System.Threading.Timer? releaseTimer;
    public void Execute(string session, RemoteCommand command)
    {
        lock (gate)
        {
            if (store?.LoginOnly == true && !TvRemote.Host.PreLoginService.Allowed(command))
                throw new InvalidOperationException("Windows sign-in supports only typing, navigation keys and the trackpad.");
            if (owner != null && owner != session && command.Type.StartsWith("mouse_")) throw new InvalidOperationException("Another remote is dragging.");
            switch (command.Type)
            {
                case "mouse_move": mouse.Move(command.Dx, command.Dy); if (owner == session) releaseTimer?.Change(15_000, Timeout.Infinite); break;
                case "mouse_click": if (buttons.Contains(command.Button!)) throw new InvalidOperationException("Release the drag before clicking."); mouse.Click(command.Button!); break;
                case "mouse_down":
                    if (buttons.Add(command.Button!)) mouse.Button(command.Button!, true);
                    owner = session;
                    releaseTimer ??= new(_ => { lock (gate) ReleaseAll(); }, null, Timeout.Infinite, Timeout.Infinite);
                    releaseTimer.Change(15_000, Timeout.Infinite); break;
                case "mouse_up": if (owner == session && buttons.Remove(command.Button!)) mouse.Button(command.Button!, false); if (buttons.Count == 0) { owner = null; releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite); } break;
                case "scroll": mouse.Scroll(command.Delta, command.Horizontal); break;
                case "key": keyboard.Press(command.Key!, command.Modifiers!); break;
                case "text": keyboard.Text(command.Value!); break;
                case "text_edit": keyboard.Edit(command.Before, command.Remove, command.Value!, command.After); break;
                case "volume": volume.Press(command.Action!); break;
                case "app": apps.Launch(command.Id!); break;
                case "playnite_close": apps.ClosePlaynite(); break;
                case "power": ReleaseAll(); power.Execute(command.Action!); break;
                case "release": Release(session); break;
            }
        }
    }
    public void Release(string session) { lock (gate) if (owner == session) ReleaseAll(); }
    private void ReleaseAll()
    {
        foreach (var button in buttons.ToArray()) { try { mouse.Button(button, false); } catch { /* Session cleanup must proceed even on a locked desktop. */ } }
        buttons.Clear(); owner = null; releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }
    public void Dispose() { lock (gate) { ReleaseAll(); releaseTimer?.Dispose(); } }
}
