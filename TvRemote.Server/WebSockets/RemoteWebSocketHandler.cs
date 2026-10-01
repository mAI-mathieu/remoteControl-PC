using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using TvRemote.Configuration;
using TvRemote.Models;
using TvRemote.Services;

namespace TvRemote.WebSockets;
public sealed class RemoteWebSocketHandler(PairingService pairing, IConfigStore store, CommandDispatcher dispatcher,
    IAppLauncherService apps, ITextFocusService focus, ILogger<RemoteWebSocketHandler> logger)
{
    private readonly ConcurrentDictionary<string, (string Device, WebSocket Socket)> sessions = new();
    private readonly SemaphoreSlim slots = new(16);
    public string[] ConnectedDeviceIds => sessions.Values.Select(s => s.Device).Distinct().ToArray();
    public void Disconnect(string id) { foreach (var session in sessions.Where(s => s.Value.Device == id)) { dispatcher.Release(session.Key); session.Value.Socket.Abort(); } }
    private static Task Send(WebSocket socket, object value, CancellationToken ct) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web), WebSocketMessageType.Text, true, ct);
    private static async Task<byte[]?> Receive(WebSocket socket, CancellationToken ct, int limit)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > limit) throw new FormatException("Invalid frame or message too large.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return stream.ToArray();
        }
    }
    public async Task Handle(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (!await slots.WaitAsync(0)) { context.Response.StatusCode = 429; return; }
        var sessionId = Guid.NewGuid().ToString("N");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var outgoingGate = new SemaphoreSlim(1);
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var updates = Channel.CreateBounded<TextFocusState>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        Action<TextFocusState> onFocus = state => updates.Writer.TryWrite(state);
        var subscribed = false;
        Task focusPump = Task.CompletedTask;
        async Task Reply(object value)
        {
            await outgoingGate.WaitAsync(sessionStop.Token);
            try { await Send(socket, value, sessionStop.Token); }
            finally { outgoingGate.Release(); }
        }
        async Task PumpFocus()
        {
            try
            {
                await foreach (var state in updates.Reader.ReadAllAsync(sessionStop.Token))
                {
                    if (socket.State != WebSocketState.Open) break;
                    await Reply(new { type = "input_focus", state });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
            { if (!sessionStop.IsCancellationRequested) socket.Abort(); }
        }
        try
        {
            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            authTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var data = await Receive(socket, authTimeout.Token, 1024);
            if (data == null) return;
            using var auth = JsonDocument.Parse(data);
            var fields = auth.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 2 || fields.Select(p => p.Name).Distinct().Count() != 2 ||
                !auth.RootElement.TryGetProperty("type", out var type) || type.GetString() != "auth" ||
                !auth.RootElement.TryGetProperty("token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
                throw new FormatException("Authentication required.");
            var token = tokenElement.GetString()!;
            var device = pairing.Authenticate(token);
            if (device == null) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Pairing required", context.RequestAborted); return; }
            sessions[sessionId] = (device.Id, socket);
            lock (store.Current) { device.LastConnected = DateTimeOffset.UtcNow; store.Save(); }
            logger.LogInformation("Device {DeviceId} connected", device.Id);
            focus.Changed += onFocus; subscribed = true;
            await Reply(new { type = "ready", deviceName = store.Current.DeviceName, apps = apps.List(), inputFocus = focus.Current });
            focusPump = PumpFocus(); focus.RequestRefresh();
            var window = Environment.TickCount64; int count = 0, errors = 0, textCharacters = 0, launches = 0;
            while (socket.State == WebSocketState.Open)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                idle.CancelAfter(TimeSpan.FromSeconds(35));
                data = await Receive(socket, idle.Token, CommandParser.MaxMessageBytes);
                if (data == null) break;
                if (pairing.Authenticate(token) == null) break;
                if (Environment.TickCount64 - window >= 1000) { window = Environment.TickCount64; count = textCharacters = launches = 0; }
                if (++count > 160) throw new FormatException("Command rate exceeded.");
                try
                {
                    var command = CommandParser.Parse(data);
                    if (command.Type == "text" && (textCharacters += command.Value!.Length) > 20_000) throw new FormatException("Text rate exceeded.");
                    if (command.Type is "app" or "power" or "playnite_close" && ++launches > 4) throw new FormatException("Action rate exceeded.");
                    dispatcher.Execute(sessionId, command);
                    if (command.Type is "mouse_click" or "mouse_up" or "app" || command.Type == "key" && command.Key is "TAB" or "ENTER" or "ESCAPE") focus.RequestRefresh();
                    if (command.Type == "ping") await Reply(new { type = "pong", inputFocus = focus.Current });
                }
                catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Never log command payloads: they may contain text or passwords.
                    logger.LogWarning("Remote command rejected ({Reason})", ex.GetType().Name);
                    await Reply(new { type = "error", message = ex is JsonException ? "Malformed command." : ex.Message });
                    if (++errors >= 5) break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or FormatException or JsonException or InvalidOperationException)
        { logger.LogInformation("Remote session ended ({Reason})", ex.GetType().Name); }
        catch (Exception ex) { logger.LogError("Remote session failed ({Reason})", ex.GetType().Name); }
        finally
        {
            if (subscribed) focus.Changed -= onFocus;
            updates.Writer.TryComplete(); sessionStop.Cancel(); await focusPump;
            dispatcher.Release(sessionId); sessions.TryRemove(sessionId, out _); slots.Release();
            if (socket.State == WebSocketState.Open) { try { using var timeout = new CancellationTokenSource(1000); await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session ended", timeout.Token); } catch { socket.Abort(); } }
        }
    }
}
