using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TvRemote.Configuration;
using TvRemote.Host;
using TvRemote.Services;

static class Preview
{
    public static async Task Run(bool nativeFocus = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TvRemote-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(directory); store.Current.ServerPort = 18765;
            var simulatedFocus = new FakeTextFocus();
            await using var server = new ServerRuntime(store, new DiscoveryService());
            await server.StartAsync([IPAddress.Loopback], services =>
            {
                services.AddSingleton<IInputService>(nativeFocus ? new FakeInput() : new PreviewInput(simulatedFocus)); services.AddSingleton<IPowerService>(new FakePower()); services.AddSingleton<IAppLauncherService>(new PreviewApps());
                if (nativeFocus) services.AddHostedService(p =>
                {
                    var monitor = p.GetRequiredService<TextFocusService>();
                    monitor.Changed += state => Console.WriteLine($"FOCUS editable={state.Editable}, password={state.Password}, revision={state.Revision}");
                    return monitor;
                });
                else services.AddSingleton<ITextFocusService>(simulatedFocus);
            });
            Console.WriteLine($"SAFE PREVIEW http://127.0.0.1:18765 — code {server.Pairing.CurrentCode.Code}");
            Console.WriteLine($"Native input, power and launch actions are replaced with fakes. Focus detection: {(nativeFocus ? "Windows UI Automation" : "simulated")}. Press Enter to stop.");
            if (!nativeFocus) Console.WriteLine("Left click simulates focusing a text field; Esc simulates leaving it.");
            await Task.Run(Console.ReadLine);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class PreviewInput(FakeTextFocus focus) : IInputService
    {
        public void Mouse(int dx, int dy, uint flags, int data = 0) { if (flags == 4) focus.Set(true); if (flags == 16) focus.Set(false); }
        public void Key(ushort key, bool up = false, bool unicode = false) { if (key == 0x1B && up && !unicode) focus.Set(false); }
        public void BatchKeys(IEnumerable<(ushort Key, bool Up, bool Unicode)> keys) { }
    }
    private sealed class PreviewApps : IAppLauncherService
    {
        public object[] List() => RemoteConfig.Defaults().Select(a => (object)new { a.Id,a.Name,a.Icon,available = a.Id != "kodi" }).ToArray();
        public void Launch(string id) { }
        public void ClosePlaynite() { }
    }
}
