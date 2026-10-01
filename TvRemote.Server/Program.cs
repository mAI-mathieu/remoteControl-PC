using TvRemote.Configuration;
using TvRemote.Host;
using TvRemote.Services;

namespace TvRemote;
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // SYSTEM workers never initialize WinForms or display error dialogs on protected desktops.
        if (args.Contains("--sign-in-service")) { PreLoginService.Run(); return; }
        if (args is ["--sign-in-helper", var pipe]) { SignInInput.RunHelper(pipe); return; }
        if (args is ["--install-sign-in", var packet, var sid])
        {
            try { PreLoginSetup.Install(packet, sid); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Windows sign-in setup failed"); }
            return;
        }
        if (args is ["--remove-sign-in"])
        {
            try { PreLoginSetup.Remove(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Windows sign-in removal failed"); }
            return;
        }
        using var singleton = new Mutex(true, @"Local\TvRemote-" + Environment.UserName, out var first);
        if (!first) { MessageBox.Show("TV Remote is already running. Open it from the system tray.", "TV Remote"); return; }
        try
        {
            var store = new ConfigStore();
            if (args.Contains("--headless")) { RunHeadless(store).GetAwaiter().GetResult(); return; }
            ApplicationConfiguration.Initialize();
            System.Windows.Forms.Application.Run(new TrayApplication(store));
        }
        catch (Exception ex) { MessageBox.Show("TV Remote could not start. Your configuration has been preserved.\n\n" + ex.Message, "TV Remote"); }
    }
    private static async Task RunHeadless(ConfigStore store)
    {
        await using var server = new ServerRuntime(store, new DiscoveryService());
        await server.StartAsync();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.WriteLine($"TV Remote running. Pairing code: {server.Pairing.CurrentCode.Code}");
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    }
}
