using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TvRemote.Configuration;
using TvRemote.Host;
using TvRemote.Models;
using TvRemote.Services;
using TvRemote.WebSockets;

if (args.Contains("--preview")) { await Preview.Run(args.Contains("--native-focus")); return; }
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
void Reject(string json) { try { CommandParser.Parse(Encoding.UTF8.GetBytes(json)); throw new Exception("Accepted invalid command: " + json); } catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException) { passed++; } }
var temp = Path.Combine(Path.GetTempPath(), "TvRemote-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    Check(CommandParser.Parse("{\"type\":\"mouse_move\",\"dx\":8,\"dy\":-3}"u8).Dx == 8, "pointer command parsing");
    Check(CommandParser.Parse(Encoding.UTF8.GetBytes("{\"type\":\"text\",\"value\":\"Příliš žluťoučký kůň 🛋️\"}")).Value!.Contains('ů'), "Unicode command parsing");
    foreach (var invalid in new[] { "{}", "[]", "{", "{\"type\":\"shell\",\"command\":\"cmd.exe\"}", "{\"type\":\"mouse_move\",\"dx\":2001,\"dy\":0}", "{\"type\":\"mouse_move\",\"dx\":1.5,\"dy\":0}", "{\"type\":\"mouse_click\",\"button\":\"middle\"}", "{\"type\":\"text\",\"value\":\"x\",\"extra\":true}", "{\"type\":\"ping\",\"type\":\"ping\"}", "{\"type\":\"key\",\"key\":\"CTRL\"}", "{\"type\":\"key\",\"key\":\"DELETE\",\"modifiers\":[\"CTRL\",\"ALT\"]}", "{\"type\":\"power\",\"action\":\"shutdown\"}", "{\"type\":\"volume\",\"action\":\"arbitrary\"}", "{\"type\":\"key\",\"key\":\"C\",\"modifiers\":[\"CTRL\",\"CTRL\"]}" }) Reject(invalid);
    Reject(JsonSerializer.Serialize(new { type = "text", value = new string('x', 10001) }));
    Reject("{\"type\":\"text\",\"value\":\"\\uD800\"}");
    Check(CommandParser.Parse("{\"type\":\"power\",\"action\":\"shutdown\",\"confirm\":true}"u8).Confirm, "explicit power confirmation");
    var store = new ConfigStore(temp); store.Current.DeviceName = "Test PC"; store.Save();
    Check(new ConfigStore(temp).Current.DeviceName == "Test PC", "configuration round trip");
    var clock = new TestClock(); var pairing = new PairingService(store, clock);
    var code = pairing.GenerateCode(); Check(code.Code.Length == 6, "six digit code");
    Check(pairing.Pair("wrong", "phone") == null, "wrong pairing code rejected");
    var token = pairing.Pair(code.Code, "Test phone")!;
    Check(token.Length == 64 && token.All(Uri.IsHexDigit), "256 bit token generation");
    Check(!File.ReadAllText(store.FilePath).Contains(token), "token is not stored in plaintext");
    Check(pairing.Authenticate(token)?.Name == "Test phone", "token authentication");
    Check(new PairingService(new ConfigStore(temp), clock).Authenticate(token) != null, "DPAPI token survives restart");
    Check(pairing.Pair(code.Code, "second") == null, "pairing code is single use");
    pairing.Revoke(pairing.Authenticate(token)!.Id); Check(pairing.Authenticate(token) == null, "revocation");
    code = pairing.GenerateCode(); clock.Advance(TimeSpan.FromMinutes(6)); Check(pairing.Pair(code.Code, "phone") == null, "expired code rejected");
    code = pairing.GenerateCode(); for (var i = 0; i < 10; i++) pairing.Pair("invalid", "phone"); Check(pairing.Pair(code.Code, "phone") == null, "pairing attempt ceiling");
    var tokens = new HashSet<string>();
    for (var i = 0; i < 20; i++) { code = pairing.GenerateCode(); tokens.Add(pairing.Pair(code.Code, "phone")!); }
    Check(tokens.Count == 20, "unique random tokens");
    foreach (var shortcut in new[] { new AppShortcut("bad", "Bad", "", "url", Url:"file:///C:/Windows"), new AppShortcut("bad", "Bad", "", "executable", "cmd.exe"), new AppShortcut("bad", "Bad", "", "executable", @"\\server\share\bad.exe"), new AppShortcut("bad", "Bad", "", "executable", @"C:\script.bat"), new AppShortcut("bad", "Bad", "", "url", Url:"javascript:alert(1)") })
    {
        try { AppLauncherService.Validate(shortcut); throw new Exception("Accepted unsafe shortcut"); } catch (InvalidDataException) { passed++; }
    }
    AppLauncherService.Validate(new("test", "Test", "", "executable", @"C:\Program Files\Missing.exe")); passed++;
    Check(!new AppLauncherService(store, NullLogger<AppLauncherService>.Instance).List().Any(a => a.ToString()!.Contains("protectedToken")), "app list does not expose secrets");
    Check(DiscoveryService.IsLan(IPAddress.Parse("192.168.1.2")) && !DiscoveryService.IsLan(IPAddress.Parse("8.8.8.8")), "LAN address filtering");
    var nativeFocusLifecycle = new TextFocusService(NullLogger<TextFocusService>.Instance);
    await nativeFocusLifecycle.StartAsync(CancellationToken.None); await nativeFocusLifecycle.StopAsync(CancellationToken.None);
    nativeFocusLifecycle.Dispose(); nativeFocusLifecycle.Dispose(); nativeFocusLifecycle.RequestRefresh();
    Check(true, "native focus monitor shuts down safely through multiple DI registrations");
    var input = new FakeInput(); var keyboard = new KeyboardService(input);
    keyboard.Text("č🛋"); Check(input.Events.Count == 6 && input.Events.All(e => e.Unicode), "UTF-16 Unicode injection");
    input.Events.Clear(); keyboard.Press("C", ["CTRL"]); Check(input.Events.Select(e => (e.Key,e.Up)).SequenceEqual(new[] { ((ushort)17,false),((ushort)67,false),((ushort)67,true),((ushort)17,true) }), "shortcut modifiers released in order");
    var mouse = new FakeMouse(); using var dispatcher = new CommandDispatcher(mouse, keyboard, new VolumeService(input), new FakeApps(), new FakePower());
    Reject("{\"type\":\"media\",\"action\":\"play_pause\"}");
    dispatcher.Execute("one", new("mouse_down", Button:"left"));
    try { dispatcher.Execute("two", new("mouse_move", Dx:1)); throw new Exception("Drag collision allowed"); } catch (InvalidOperationException) { passed++; }
    dispatcher.Release("two"); Check(mouse.Down, "another session cannot release drag"); dispatcher.Release("one"); Check(!mouse.Down, "disconnect releases drag");
    using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); store.Current.ServerPort = ((IPEndPoint)listener.LocalEndpoint).Port; }
    if (store.Current.ServerPort == store.Current.HttpsPort) store.Current.HttpsPort++;
    await using var server = new ServerRuntime(store, new DiscoveryService());
    var focus = new FakeTextFocus();
    await server.StartAsync([IPAddress.Loopback], services => { services.AddSingleton<IInputService>(input); services.AddSingleton<IPowerService>(new FakePower()); services.AddSingleton<IAppLauncherService>(new FakeApps()); services.AddSingleton<ITextFocusService>(focus); });
    var origin = $"http://127.0.0.1:{store.Current.ServerPort}";
    using var http = new HttpClient();
    var shell = await http.GetStringAsync(origin + "/");
    Check(shell.Contains("data-volume=\"up\"") && !shell.Contains("view-media") && !shell.Contains("data-nav=\"keyboard\"><span>"), "simplified shell with main-page volume and contextual keyboard");
    var hostile = new HttpRequestMessage(HttpMethod.Post, origin + "/api/pair"); hostile.Headers.Add("Origin", "https://evil.example"); hostile.Content = new StringContent("{}", Encoding.UTF8, "application/json");
    Check((await http.SendAsync(hostile)).StatusCode == HttpStatusCode.Forbidden, "cross-origin pairing blocked");
    var rebound = new HttpRequestMessage(HttpMethod.Get, origin + "/"); rebound.Headers.Host = "evil.example";
    Check((await http.SendAsync(rebound)).StatusCode == HttpStatusCode.Forbidden, "DNS rebinding host blocked");
    using (var unauth = new ClientWebSocket())
    {
        unauth.Options.SetRequestHeader("Origin", origin); await unauth.ConnectAsync(new Uri(origin.Replace("http", "ws") + "/ws"), CancellationToken.None);
        await WsSend(unauth, new { type = "auth", token = new string('0', 64) });
        var buffer = new byte[4096]; using var timeout = new CancellationTokenSource(3000); var result = await unauth.ReceiveAsync(buffer, timeout.Token);
        Check(result.MessageType == WebSocketMessageType.Close && unauth.CloseStatus == WebSocketCloseStatus.PolicyViolation, "unauthenticated WebSocket rejected");
    }
    code = server.Pairing.CurrentCode;
    var pairRequest = new HttpRequestMessage(HttpMethod.Post, origin + "/api/pair"); pairRequest.Headers.Add("Origin", origin); pairRequest.Content = new StringContent(JsonSerializer.Serialize(new { code = code.Code, name = "Socket phone" }), Encoding.UTF8, "application/json");
    var pairResponse = await http.SendAsync(pairRequest); using var pairJson = JsonDocument.Parse(await pairResponse.Content.ReadAsStringAsync()); token = pairJson.RootElement.GetProperty("token").GetString()!;
    Check(pairResponse.IsSuccessStatusCode, "HTTP pairing exchange");
    using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Origin", origin); await ws.ConnectAsync(new Uri(origin.Replace("http", "ws") + "/ws"), CancellationToken.None);
    await WsSend(ws, new { type = "auth", token });
    var readyMessage = await WsReceive(ws); Check(readyMessage.Contains("ready"), "authenticated WebSocket ready");
    using (var readyJson = JsonDocument.Parse(readyMessage))
    {
        var appJson = readyJson.RootElement.GetProperty("apps")[0];
        Check(appJson.GetProperty("id").GetString() == "test" && appJson.GetProperty("name").GetString() == "Test app", "camelCase app contract for browser");
        Check(!readyJson.RootElement.GetProperty("inputFocus").GetProperty("editable").GetBoolean(), "initial input focus included in authenticated handshake");
    }
    focus.Set(true, true);
    using (var focusJson = JsonDocument.Parse(await WsReceive(ws)))
    {
        var state = focusJson.RootElement.GetProperty("state");
        Check(focusJson.RootElement.GetProperty("type").GetString() == "input_focus" && state.GetProperty("editable").GetBoolean() && state.GetProperty("password").GetBoolean() && state.EnumerateObject().Count() == 3, "focus notifications contain capability metadata only");
    }
    focus.Set(false);
    using (var focusJson = JsonDocument.Parse(await WsReceive(ws))) Check(!focusJson.RootElement.GetProperty("state").GetProperty("editable").GetBoolean(), "leaving editable focus is sent to the phone");
    input.Events.Clear(); await WsSend(ws, new { type = "volume", action = "up" }); await WsSend(ws, new { type = "ping" });
    Check((await WsReceive(ws)).Contains("pong") && input.Events.Select(e => (e.Key,e.Up)).SequenceEqual(new[] { ((ushort)0xAF,false),((ushort)0xAF,true) }), "main-page volume command reaches Windows volume key service");
    input.Events.Clear(); await WsSend(ws, new { type = "text", value = "Red Dead Redemption 2" }); await WsSend(ws, new { type = "ping" }); Check((await WsReceive(ws)).Contains("pong") && input.Events.Count == 42, "Unicode input reaches injected service through WebSocket");
    await WsSend(ws, new { type = "shell", command = "echo no" }); Check((await WsReceive(ws)).Contains("error"), "malformed command rejected on live socket");
    server.Pairing.Revoke(server.Pairing.Authenticate(token)!.Id);
    try { await WsSend(ws, new { type = "ping" }); await WsReceive(ws); throw new Exception("Revoked socket survived"); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { passed++; Console.WriteLine("PASS: revocation terminates live WebSocket"); }
    var cert = new LocalCertificateService(store, new DiscoveryService()); cert.Generate(); using var certificate = cert.Load();
    Check(certificate.HasPrivateKey && File.Exists(cert.RootPath) && !File.ReadAllBytes(cert.PfxPath).AsSpan().StartsWith(new byte[] { 0x30, 0x82 }), "local HTTPS certificate with DPAPI protected private key");
    var certificates = X509CertificateLoader.LoadPkcs12Collection(ProtectedData.Unprotect(File.ReadAllBytes(cert.PfxPath),null,DataProtectionScope.CurrentUser),null);
    Check(certificates.Count == 2 && certificates.Count(c => c.HasPrivateKey) == 1, "root signing private key is discarded");
    foreach (var item in certificates) item.Dispose();
    var hostLog = File.ReadAllText(Path.Combine(temp,"host.log"));
    Check(hostLog.Contains("Server started") && hostLog.Contains("Pairing attempt accepted") && !hostLog.Contains(token) && !hostLog.Contains("Red Dead Redemption 2") && !hostLog.Contains("input_focus"), "startup/pairing logging without tokens, typed text or focus details");
    var mdnsPacket = MdnsService.BuildResponse([IPAddress.Parse("192.168.1.50")],8123);
    Check(mdnsPacket[2] == 0x84 && mdnsPacket[7] == 4 && Encoding.ASCII.GetString(mdnsPacket).Contains("tvpc"), "mDNS A/PTR/SRV/TXT response structure");
    var invalidConfig = Path.Combine(temp, "invalid"); Directory.CreateDirectory(invalidConfig); File.WriteAllText(Path.Combine(invalidConfig,"config.json"), "{\"serverPort\":80}");
    try { _ = new ConfigStore(invalidConfig); throw new Exception("Bad config accepted"); } catch (InvalidDataException) { passed++; }
    Console.WriteLine($"All {passed} checks passed. No native mouse, keyboard, application or power actions executed.");
}
finally { Directory.Delete(temp, true); }
static async Task WsSend(ClientWebSocket ws, object data) => await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(data), WebSocketMessageType.Text, true, CancellationToken.None);
static async Task<string> WsReceive(ClientWebSocket ws) { var buffer = new byte[4096]; using var timeout = new CancellationTokenSource(3000); var result = await ws.ReceiveAsync(buffer, timeout.Token); return Encoding.UTF8.GetString(buffer,0,result.Count); }
sealed class TestClock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan delta) => now += delta; }
sealed class FakeInput : IInputService
{
    public List<(ushort Key,bool Up,bool Unicode)> Events { get; } = [];
    public void Key(ushort key, bool up = false, bool unicode = false) => Events.Add((key,up,unicode));
    public void BatchKeys(IEnumerable<(ushort Key,bool Up,bool Unicode)> keys) => Events.AddRange(keys);
    public void Mouse(int dx,int dy,uint flags,int data = 0) { }
}
sealed class FakeMouse : IMouseService { public bool Down; public void Move(int x,int y) { } public void Click(string button) { } public void Button(string button,bool down) => Down = down; public void Scroll(int v,int h) { } }
sealed class FakeApps : IAppLauncherService { public object[] List() => [new { Id = "test", Name = "Test app", Icon = "folder", available = true }]; public void Launch(string id) { } public void ClosePlaynite() { } }
sealed class FakePower : IPowerService { public void Execute(string action) { } }
sealed class FakeTextFocus : ITextFocusService
{
    public TextFocusState Current { get; private set; } = new(false);
    public event Action<TextFocusState>? Changed;
    public void RequestRefresh() { }
    public void Set(bool editable, bool password = false) { Current = new(editable, password, Current.Revision + 1); Changed?.Invoke(Current); }
}
