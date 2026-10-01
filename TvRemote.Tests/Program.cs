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
if (args.Contains("--manage-apps-preview")) { Preview.RunAppManager(); return; }
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
    Check(CommandParser.Parse("{\"type\":\"text_edit\",\"before\":-2,\"remove\":2,\"value\":\"he\",\"after\":0}"u8).Remove == 2, "bounded live text editing schema");
    foreach (var invalidEdit in new[] { "{\"type\":\"text_edit\",\"before\":0,\"remove\":-1,\"value\":\"\",\"after\":0}", "{\"type\":\"text_edit\",\"before\":0,\"remove\":0,\"value\":\"\",\"after\":0}", "{\"type\":\"text_edit\",\"before\":10000,\"remove\":1,\"value\":\"\",\"after\":0}", "{\"type\":\"text_edit\",\"before\":0,\"remove\":1,\"value\":\"\\uD800\",\"after\":0}" }) Reject(invalidEdit);
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
    var website = ShortcutManagement.Create(" TV guide ", "globe", "url", " example.com ");
    Check(website.Name == "TV guide" && website.Url == "https://example.com", "website manager trims names and adds HTTPS");
    var editedWebsite = ShortcutManagement.Create("Edited guide", "film", "url", "https://example.com/tv", website.Id);
    Check(editedWebsite.Id == website.Id && editedWebsite.Url!.EndsWith("/tv"), "editing keeps shortcut identity");
    var program = ShortcutManagement.Create("Explorer", "folder", "executable", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
    var programPng = ProgramIcons.GetPng(program.Path);
    Check(programPng != null && programPng.AsSpan().StartsWith(new byte[] { 137,80,78,71,13,10,26,10 }), "actual Windows executable icon is extracted as PNG");
    Check(ReferenceEquals(programPng, ProgramIcons.GetPng(program.Path)) && ProgramIcons.GetPng(@"\\server\share\app.exe") == null && ProgramIcons.GetPng("https://example.com") == null, "program icons are cached locally and reject nonlocal sources");
    var devicesBefore = store.Current.PairedDevices.Count;
    ShortcutManagement.Save(store, [program, editedWebsite]);
    var savedApps = new ConfigStore(temp).Current;
    Check(savedApps.AppShortcuts.Select(a => a.Id).SequenceEqual(new[] { program.Id, website.Id }) && savedApps.PairedDevices.Count == devicesBefore && savedApps.ServerPort == store.Current.ServerPort, "saving app order preserves phones and server settings");
    ShortcutManagement.Save(store, [editedWebsite]);
    Check(new ConfigStore(temp).Current.AppShortcuts.Count == 1 && InstalledAppDiscovery.IsLocalProgram(program.Path), "removing a shortcut preserves its program file");
    try { ShortcutManagement.Save(store, [website, editedWebsite]); throw new Exception("Duplicate shortcuts accepted"); } catch (InvalidDataException) { passed++; }
    try { ShortcutManagement.Create("Script", "folder", "executable", @"C:\test.bat"); throw new Exception("Script accepted"); } catch (InvalidDataException) { passed++; }
    var failingStore = new FailingConfigStore(); var originalApps = failingStore.Current.AppShortcuts;
    try { ShortcutManagement.Save(failingStore, [website]); throw new Exception("Save unexpectedly succeeded"); } catch (IOException) { }
    Check(ReferenceEquals(failingStore.Current.AppShortcuts, originalApps), "failed app save restores the previous list");
    var detectedApps = await InstalledAppDiscovery.FindAsync();
    Check(detectedApps.All(a => InstalledAppDiscovery.IsLocalProgram(a.Path)) && detectedApps.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == detectedApps.Length, "installed app discovery returns unique local programs without launching them");
    Exception? dialogError = null;
    var dialogThread = new Thread(() =>
    {
        try
        {
            using var addWebsite = new ShortcutEditorForm(website, true);
            using var addProgram = new ShortcutEditorForm(program, true);
            using var editProgram = new ShortcutEditorForm(program, false);
            using var manager = new AppManagerForm(store);
            using var picker = new InstalledAppPickerForm();
        }
        catch (Exception ex) { dialogError = ex; }
    });
    dialogThread.SetApartmentState(ApartmentState.STA); dialogThread.Start(); dialogThread.Join();
    if (dialogError != null) throw new Exception("App manager dialog initialization failed", dialogError);
    Check(true, "PC manager, program picker and all shortcut editor dialogs initialize safely");
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
    input.Events.Clear(); keyboard.Edit(-1, 1, "é\n", 1);
    Check(input.Events.Select(e => (e.Key,e.Up,e.Unicode)).SequenceEqual(new[] { ((ushort)0x25,false,false),((ushort)0x25,true,false),((ushort)8,false,false),((ushort)8,true,false),((ushort)'é',false,true),((ushort)'é',true,true),((ushort)13,false,false),((ushort)13,true,false),((ushort)0x27,false,false),((ushort)0x27,true,false) }), "live correction moves caret, removes text, inserts Unicode and presses Enter in order");
    var mouse = new FakeMouse(); using var dispatcher = new CommandDispatcher(mouse, keyboard, new VolumeService(input), new FakeApps(), new FakePower());
    Reject("{\"type\":\"media\",\"action\":\"play_pause\"}");
    dispatcher.Execute("one", new("mouse_down", Button:"left"));
    try { dispatcher.Execute("two", new("mouse_move", Dx:1)); throw new Exception("Drag collision allowed"); } catch (InvalidOperationException) { passed++; }
    dispatcher.Release("two"); Check(mouse.Down, "another session cannot release drag"); dispatcher.Release("one"); Check(!mouse.Down, "disconnect releases drag");
    using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); store.Current.ServerPort = ((IPEndPoint)listener.LocalEndpoint).Port; }
    if (store.Current.ServerPort == store.Current.HttpsPort) store.Current.HttpsPort++;
    await using var server = new ServerRuntime(store, new DiscoveryService());
    var focus = new FakeTextFocus();
    var socketApps = new FakeApps();
    await server.StartAsync([IPAddress.Loopback], services => { services.AddSingleton<IInputService>(input); services.AddSingleton<IPowerService>(new FakePower()); services.AddSingleton<IAppLauncherService>(socketApps); services.AddSingleton<ITextFocusService>(focus); });
    var origin = $"http://127.0.0.1:{store.Current.ServerPort}";
    using var http = new HttpClient();
    var shell = await http.GetStringAsync(origin + "/");
    Check(shell.Contains("data-volume=\"up\"") && !shell.Contains("view-media") && !shell.Contains("data-nav=\"keyboard\"><span>"), "simplified shell with main-page volume and contextual keyboard");
    var hostile = new HttpRequestMessage(HttpMethod.Post, origin + "/api/pair"); hostile.Headers.Add("Origin", "https://evil.example"); hostile.Content = new StringContent("{}", Encoding.UTF8, "application/json");
    Check((await http.SendAsync(hostile)).StatusCode == HttpStatusCode.Forbidden, "cross-origin pairing blocked");
    var rebound = new HttpRequestMessage(HttpMethod.Get, origin + "/"); rebound.Headers.Host = "evil.example";
    Check((await http.SendAsync(rebound)).StatusCode == HttpStatusCode.Forbidden, "DNS rebinding host blocked");
    var anonymousIcon = new HttpRequestMessage(HttpMethod.Post, origin + "/api/app-icon/test"); anonymousIcon.Headers.Add("Origin", origin);
    Check((await http.SendAsync(anonymousIcon)).StatusCode == HttpStatusCode.Unauthorized, "program icons require a paired phone token");
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
    var iconRequest = new HttpRequestMessage(HttpMethod.Post, origin + "/api/app-icon/test"); iconRequest.Headers.Add("Origin", origin); iconRequest.Headers.Authorization = new("Bearer", token);
    var iconResponse = await http.SendAsync(iconRequest);
    Check(iconResponse.IsSuccessStatusCode && iconResponse.Content.Headers.ContentType?.MediaType == "image/png" && (await iconResponse.Content.ReadAsByteArrayAsync()).SequenceEqual(programPng!), "paired phone loads the actual program icon without a token or executable path in the URL");
    var missingIcon = new HttpRequestMessage(HttpMethod.Post, origin + "/api/app-icon/missing"); missingIcon.Headers.Add("Origin", origin); missingIcon.Headers.Authorization = new("Bearer", token);
    Check((await http.SendAsync(missingIcon)).StatusCode == HttpStatusCode.NotFound, "unknown program icons return not found");
    using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Origin", origin); await ws.ConnectAsync(new Uri(origin.Replace("http", "ws") + "/ws"), CancellationToken.None);
    await WsSend(ws, new { type = "auth", token });
    var readyMessage = await WsReceive(ws); Check(readyMessage.Contains("ready"), "authenticated WebSocket ready");
    using (var readyJson = JsonDocument.Parse(readyMessage))
    {
        var appJson = readyJson.RootElement.GetProperty("apps")[0];
        Check(appJson.GetProperty("id").GetString() == "test" && appJson.GetProperty("name").GetString() == "Test app", "camelCase app contract for browser");
        Check(!readyJson.RootElement.GetProperty("inputFocus").GetProperty("editable").GetBoolean(), "initial input focus included in authenticated handshake");
        Check(readyJson.RootElement.GetProperty("install").GetProperty("httpsPort").GetInt32() == store.Current.HttpsPort, "Android install setup includes the configured HTTPS port");
    }
    socketApps.Apps = [new { id = "updated", name = "Edited from PC", icon = "globe", available = true }];
    server.Handler!.NotifyAppsChanged();
    using (var appsJson = JsonDocument.Parse(await WsReceive(ws)))
        Check(appsJson.RootElement.GetProperty("type").GetString() == "apps_changed" && appsJson.RootElement.GetProperty("apps")[0].GetProperty("name").GetString() == "Edited from PC", "PC app updates reach connected phones without restarting the server");
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
    input.Events.Clear(); await WsSend(ws, new { type = "text_edit", before = 0, remove = 2, value = "he", after = 0 }); await WsSend(ws, new { type = "ping" });
    Check((await WsReceive(ws)).Contains("pong") && input.Events.Count == 8 && input.Events.Take(4).All(e => e.Key == 8 && !e.Unicode) && input.Events.Skip(4).All(e => e.Unicode), "phone text corrections reach the PC through authenticated WebSocket");
    await WsSend(ws, new { type = "shell", command = "echo no" }); Check((await WsReceive(ws)).Contains("error"), "malformed command rejected on live socket");
    server.Pairing.Revoke(server.Pairing.Authenticate(token)!.Id);
    var revokedIcon = new HttpRequestMessage(HttpMethod.Post, origin + "/api/app-icon/test"); revokedIcon.Headers.Add("Origin", origin); revokedIcon.Headers.Authorization = new("Bearer", token);
    Check((await http.SendAsync(revokedIcon)).StatusCode == HttpStatusCode.Unauthorized, "revoked phones lose program icon access");
    try { await WsSend(ws, new { type = "ping" }); await WsReceive(ws); throw new Exception("Revoked socket survived"); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { passed++; Console.WriteLine("PASS: revocation terminates live WebSocket"); }
    var cert = new LocalCertificateService(store, new DiscoveryService()); cert.Generate(); using var certificate = cert.Load();
    Check(certificate.HasPrivateKey && File.Exists(cert.RootPath) && !File.ReadAllBytes(cert.PfxPath).AsSpan().StartsWith(new byte[] { 0x30, 0x82 }), "local HTTPS certificate with DPAPI protected private key");
    var certificates = X509CertificateLoader.LoadPkcs12Collection(ProtectedData.Unprotect(File.ReadAllBytes(cert.PfxPath),null,DataProtectionScope.CurrentUser),null);
    Check(certificates.Count == 2 && certificates.Count(c => c.HasPrivateKey) == 1, "root signing private key is discarded");
    foreach (var item in certificates) item.Dispose();
    var loginDirectory = Path.Combine(temp, "sign-in"); Directory.CreateDirectory(loginDirectory);
    store.Current.EnableHttps = true; code = pairing.GenerateCode(); var loginToken = pairing.Pair(code.Code, "Sign-in phone")!;
    PreLoginSetup.Export(store, loginDirectory);
    var loginStore = new ConfigStore(loginDirectory, DataProtectionScope.LocalMachine, loginOnly: true);
    Check(new PairingService(loginStore, clock).Authenticate(loginToken) != null && loginStore.Current.AppShortcuts.Count == 0, "sign-in snapshot preserves existing phone tokens with machine DPAPI and omits app launch paths");
    var snapshotBytes = File.ReadAllBytes(loginStore.FilePath); loginStore.Current.DeviceName = "Not written"; loginStore.Save();
    Check(File.ReadAllBytes(loginStore.FilePath).SequenceEqual(snapshotBytes), "service never overwrites the owner's pairing/revocation snapshot");
    Check(PreLoginService.Allowed(new("text", Value: "example")) && PreLoginService.Allowed(new("key", Key: "TAB", Modifiers: [])) && PreLoginService.Allowed(new("text_edit", Remove: 1, Value: "")), "sign-in allows credential entry and navigation");
    Check(!PreLoginService.Allowed(new("app", Id: "explorer")) && !PreLoginService.Allowed(new("power", Action: "shutdown", Confirm: true)) && !PreLoginService.Allowed(new("key", Key: "D", Modifiers: ["WIN"])) && !PreLoginService.Allowed(new("volume", Action: "up")), "sign-in rejects app launch, power, volume and Windows shortcuts");
    Check(SignInInput.Valid(new("keys", Keys: [new(0x0D, false, false), new('č', false, true)])) && !SignInInput.Valid(new("keys", Keys: [new(0x5B, false, false)])) && !SignInInput.Valid(new("mouse", int.MinValue, 0, 1)) && !SignInInput.Valid(new("keys", Keys: new SignInStroke[257])), "protected desktop helper independently bounds input and rejects Windows keys");
    var loginMouse = new FakeMouse();
    using (var loginDispatcher = new CommandDispatcher(loginMouse, new KeyboardService(input), new VolumeService(input), new FakeApps(), new FakePower(), loginStore))
    {
        try { loginDispatcher.Execute("test", new("app", Id: "test")); throw new Exception("Sign-in launched app"); } catch (InvalidOperationException) { passed++; }
        loginDispatcher.Execute("test", new("mouse_down", Button: "left")); loginDispatcher.Release("test");
        Check(!loginMouse.Down, "sign-in disconnect releases a trackpad drag");
    }
    var loginListener = new TcpListener(IPAddress.Loopback, 0); loginListener.Start(); loginStore.Current.HttpsPort = ((IPEndPoint)loginListener.LocalEndpoint).Port; loginListener.Stop();
    var loginOrigin = $"https://127.0.0.1:{loginStore.Current.HttpsPort}";
    using var loginCert = new LocalCertificateService(loginStore, new DiscoveryService()).Load();
    using var loginHttp = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer?.Thumbprint == loginCert.Thumbprint });
    await using (var loginServer = new ServerRuntime(loginStore, new DiscoveryService()))
    {
        await loginServer.StartAsync([IPAddress.Loopback], services => { services.AddSingleton<IInputService>(input); services.AddSingleton<ITextFocusService>(focus); });
        var forbiddenPair = new HttpRequestMessage(HttpMethod.Post, loginOrigin + "/api/pair"); forbiddenPair.Headers.Add("Origin", loginOrigin); forbiddenPair.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        Check((await loginHttp.SendAsync(forbiddenPair)).StatusCode == HttpStatusCode.Forbidden && (await loginHttp.GetAsync(loginOrigin + "/TV-Remote-Root.cer")).StatusCode == HttpStatusCode.NotFound, "sign-in TLS server forbids new pairing and public certificate-file reads");
        using var loginSocket = new ClientWebSocket(); loginSocket.Options.SetRequestHeader("Origin", loginOrigin); loginSocket.Options.RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == loginCert.Thumbprint;
        await loginSocket.ConnectAsync(new Uri(loginOrigin.Replace("https", "wss") + "/ws"), CancellationToken.None);
        await WsSend(loginSocket, new { type = "auth", token = loginToken });
        using var loginReady = JsonDocument.Parse(await WsReceive(loginSocket));
        Check(loginReady.RootElement.GetProperty("loginOnly").GetBoolean() && loginReady.RootElement.GetProperty("apps").GetArrayLength() == 0, "existing paired phone connects to sign-in TLS endpoint without app launchers");
        await WsSend(loginSocket, new { type = "key", key = "D", modifiers = new[] { "WIN" } });
        Check((await WsReceive(loginSocket)).Contains("error"), "privileged sign-in WebSocket rejects Windows shortcuts before injection");
        input.Events.Clear(); await WsSend(loginSocket, new { type = "text", value = "Example only" }); await WsSend(loginSocket, new { type = "ping" });
        Check((await WsReceive(loginSocket)).Contains("pong") && input.Events.Count == 24, "sign-in authenticated typing reaches fake input over TLS");
        await loginSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
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
sealed class FakeApps : IAppLauncherService { public object[] Apps = [new { Id = "test", Name = "Test app", Icon = "folder", available = true, hasProgramIcon = true }]; public object[] List() => Apps; public void Launch(string id) { } public void ClosePlaynite() { } public byte[]? GetIcon(string id) => id == "test" ? ProgramIcons.GetPng(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) : null; }
sealed class FailingConfigStore : IConfigStore { public RemoteConfig Current { get; } = new(); public void Save() => throw new IOException("Simulated write failure"); }
sealed class FakePower : IPowerService { public void Execute(string action) { } }
sealed class FakeTextFocus : ITextFocusService
{
    public TextFocusState Current { get; private set; } = new(false);
    public event Action<TextFocusState>? Changed;
    public void RequestRefresh() { }
    public void Set(bool editable, bool password = false) { Current = new(editable, password, Current.Revision + 1); Changed?.Invoke(Current); }
}
