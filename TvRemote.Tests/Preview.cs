using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TvRemote.Configuration;
using TvRemote.Host;
using TvRemote.Services;

static class Preview
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TvRemote-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(directory); store.Current.ServerPort = 18765;
            await using var server = new ServerRuntime(store, new DiscoveryService());
            await server.StartAsync([IPAddress.Loopback], services =>
            {
                services.AddSingleton<IInputService>(new FakeInput()); services.AddSingleton<IPowerService>(new FakePower()); services.AddSingleton<IAppLauncherService>(new PreviewApps());
            });
            Console.WriteLine($"SAFE PREVIEW http://127.0.0.1:18765 — code {server.Pairing.CurrentCode.Code}");
            Console.WriteLine("Native input, power and launch actions are replaced with fakes. Press Enter to stop.");
            await Task.Run(Console.ReadLine);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class PreviewApps : IAppLauncherService
    {
        public object[] List() => RemoteConfig.Defaults().Select(a => (object)new { a.Id,a.Name,a.Icon,available = a.Id != "kodi" }).ToArray();
        public void Launch(string id) { }
        public void ClosePlaynite() { }
    }
}
