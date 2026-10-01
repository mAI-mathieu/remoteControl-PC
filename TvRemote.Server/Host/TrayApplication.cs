using System.Diagnostics;
using Microsoft.Win32;
using QRCoder;
using System.Net.NetworkInformation;
using TvRemote.Configuration;
using TvRemote.Services;

namespace TvRemote.Host;
public sealed class TrayApplication : ApplicationContext
{
    private readonly ConfigStore store;
    private readonly DiscoveryService discovery;
    private readonly ServerRuntime server;
    private readonly NotifyIcon tray;
    private readonly Form window;
    private readonly Label status = new() { AutoSize = true, MaximumSize = new(630, 0) };
    private readonly Label code = new() { AutoSize = true, Font = new("Segoe UI", 24, FontStyle.Bold) };
    private readonly ListView devices = new() { View = View.Details, FullRowSelect = true, Width = 630, Height = 150 };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly Button newCode;
    private readonly Button restart;
    private readonly CheckBox autostart;
    private readonly PictureBox qr = new() { Width = 140, Height = 140, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private string? qrUrl;
    private bool quitting;
    private readonly System.Threading.Timer networkTimer;
    public TrayApplication(ConfigStore store)
    {
        this.store = store; discovery = new(); server = new(store, discovery);
        window = new() { Text = "TV Remote", Size = new(700, 720), MinimumSize = new(700, 720), StartPosition = FormStartPosition.CenterScreen, Font = new("Segoe UI", 10) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(20) };
        window.Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "TV Remote", Font = new("Segoe UI", 22, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(status); layout.Controls.Add(qr); layout.Controls.Add(code);
        newCode = Button("Generate new pairing code", () => { server.Pairing.GenerateCode(); RefreshStatus(); }); layout.Controls.Add(newCode);
        layout.Controls.Add(new Label { Text = "Paired phones — select a device to revoke", AutoSize = true });
        devices.Columns.Add("Device", 240); devices.Columns.Add("Status", 100); devices.Columns.Add("Last connected", 260);
        layout.Controls.Add(devices);
        layout.Controls.Add(Button("Revoke selected device", () => { foreach (ListViewItem item in devices.SelectedItems) server.Pairing.Revoke((string)item.Tag!); RefreshStatus(); }));
        var auto = new CheckBox { Text = "Start with Windows", Checked = store.Current.StartWithWindows, AutoSize = true };
        autostart = auto;
        if (store.Current.StartWithWindows) SetAutostart(true);
        auto.CheckedChanged += (_, _) =>
        {
            try { SetAutostart(auto.Checked); store.Current.StartWithWindows = auto.Checked; store.Save(); }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "Autostart failed"); }
        };
        layout.Controls.Add(auto);
        restart = Button("Restart server / reload configuration", () => _ = RestartAsync(true)); layout.Controls.Add(restart);
        layout.Controls.Add(Button("Open configuration", () => Process.Start(new ProcessStartInfo("notepad.exe", store.FilePath) { UseShellExecute = true })));
        layout.Controls.Add(Button("Enable local HTTPS / create certificate", () => _ = EnableHttpsAsync()));
        layout.Controls.Add(Button("Open certificate and logs folder", () => Process.Start(new ProcessStartInfo("explorer.exe", store.DirectoryPath) { UseShellExecute = true })));
        layout.Controls.Add(new Label { Text = "Allow TV Remote on Private networks in Windows Firewall. No router port forwarding is needed.\nHTTPS installation and phone testing instructions are in README.md.", AutoSize = true, MaximumSize = new(630, 0), Padding = new(0, 10, 0, 0) });
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open TV Remote", null, (_, _) => Show());
        menu.Items.Add("Generate pairing code", null, (_, _) => { if (newCode.Enabled) { server.Pairing.GenerateCode(); Show(); } });
        menu.Items.Add("Restart server", null, (_, _) => _ = RestartAsync(true));
        menu.Items.Add("Quit", null, (_, _) => _ = QuitAsync());
        tray = new() { Text = "TV Remote — starting", Icon = SystemIcons.Application, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Show();
        window.FormClosing += (_, e) => { if (!quitting) { e.Cancel = true; window.Hide(); } };
        timer.Tick += (_, _) => RefreshStatus(); timer.Start();
        networkTimer = new(_ => { if (!quitting && window.IsHandleCreated) window.BeginInvoke(() => { if (restart.Enabled) _ = RestartAsync(false); }); }, null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        window.Shown += async (_, _) => await RestartAsync(false);
        window.Show();
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new(250, 36) };
        button.Click += (_, _) => action(); return button;
    }
    private void Show() { RefreshStatus(); window.Show(); window.WindowState = FormWindowState.Normal; window.Activate(); }
    private async Task RestartAsync(bool reload)
    {
        restart.Enabled = newCode.Enabled = false; status.Text = "Starting…";
        try
        {
            await server.StopAsync();
            if (reload)
            {
                var updated = new ConfigStore(store.DirectoryPath).Current;
                store.Current.ServerPort = updated.ServerPort; store.Current.HttpsPort = updated.HttpsPort;
                store.Current.EnableHttps = updated.EnableHttps; store.Current.DeviceName = updated.DeviceName;
                store.Current.MouseSensitivity = updated.MouseSensitivity; store.Current.AppShortcuts = updated.AppShortcuts;
                store.Current.PairedDevices = updated.PairedDevices;
                store.Current.StartWithWindows = updated.StartWithWindows;
                autostart.Checked = updated.StartWithWindows;
            }
            await server.StartAsync(); newCode.Enabled = true;
            tray.Text = "TV Remote — running";
        }
        catch (Exception ex)
        {
            status.Text = "Server could not start. Check the port, network and config.json.\n" + ex.Message;
            tray.Text = "TV Remote — stopped"; MessageBox.Show(window, status.Text, "TV Remote");
        }
        finally { restart.Enabled = true; if (newCode.Enabled) RefreshStatus(); }
    }
    private void RefreshStatus()
    {
        if (!newCode.Enabled) return;
        var urls = discovery.Urls(store.Current.ServerPort);
        status.Text = $"TV Remote is {server.Status.ToLowerInvariant()}\nOpen this address on your phone:\n{string.Join("\n", urls)}\n" +
            (store.Current.EnableHttps ? "HTTPS: " + string.Join(" / ", discovery.Urls(store.Current.HttpsPort, true)) + "\n" : "") +
            "mDNS: " + server.DiscoveryStatus + "\nhttp://tvpc.local:" + store.Current.ServerPort + " (use LAN IP if unresolved)";
        var preferred = (store.Current.EnableHttps ? discovery.Urls(store.Current.HttpsPort, true) : urls).FirstOrDefault();
        qr.Visible = preferred != null;
        if (preferred != null && qrUrl != preferred)
        {
            qrUrl = preferred;
            using var stream = new MemoryStream(PngByteQRCodeHelper.GetQRCode(preferred, QRCodeGenerator.ECCLevel.Q, 5));
            using var generated = Image.FromStream(stream);
            var previous = qr.Image; qr.Image = new Bitmap(generated); previous?.Dispose();
        }
        var pairing = server.Pairing.CurrentCode;
        code.Text = pairing.Expires > DateTimeOffset.UtcNow ? $"Pairing code: {pairing.Code}  ·  {Math.Ceiling((pairing.Expires - DateTimeOffset.UtcNow).TotalMinutes)} min" : "Pairing code expired or used";
        var selected = devices.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToHashSet();
        devices.BeginUpdate(); devices.Items.Clear();
        lock (store.Current)
        {
            foreach (var device in store.Current.PairedDevices)
            {
                var item = new ListViewItem([device.Name, server.Handler?.ConnectedDeviceIds.Contains(device.Id) == true ? "Connected" : "Offline", device.LastConnected?.ToLocalTime().ToString("g") ?? "Never"]) { Tag = device.Id, Selected = selected.Contains(device.Id) };
                devices.Items.Add(item);
            }
        }
        devices.EndUpdate();
    }
    private async Task EnableHttpsAsync()
    {
        try
        {
            var certificates = new LocalCertificateService(store, discovery);
            if (!File.Exists(certificates.PfxPath)) certificates.Generate();
            store.Current.EnableHttps = true; store.Save(); await RestartAsync(false);
            MessageBox.Show(window, "Local HTTPS enabled. Install TV-Remote-Root.cer from the configuration folder on your phone, verify its SHA-256 fingerprint locally, and enable trust. Then open the HTTPS LAN address. See README for platform instructions.", "HTTPS setup");
        }
        catch (Exception ex) { MessageBox.Show(window, ex.Message, "HTTPS setup failed"); }
    }
    private void NetworkChanged(object? sender, EventArgs e) { if (!quitting) networkTimer.Change(1000, Timeout.Infinite); }
    private static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var path = Environment.ProcessPath!;
            var command = Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"\"{path}\" \"{Path.Combine(AppContext.BaseDirectory, "TvRemote.Server.dll")}\"" : $"\"{path}\"";
            key.SetValue("TvRemote", command);
        }
        else key.DeleteValue("TvRemote", false);
    }
    private async Task QuitAsync()
    {
        quitting = true; timer.Stop(); tray.Visible = false;
        NetworkChange.NetworkAddressChanged -= NetworkChanged; networkTimer.Dispose();
        await server.DisposeAsync(); qr.Image?.Dispose(); window.Close(); tray.Dispose(); timer.Dispose(); ExitThread();
    }
}
