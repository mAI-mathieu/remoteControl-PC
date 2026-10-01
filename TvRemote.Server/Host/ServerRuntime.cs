using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using TvRemote.Configuration;
using TvRemote.Services;
using TvRemote.WebSockets;

namespace TvRemote.Host;
public sealed class ServerRuntime(ConfigStore store, DiscoveryService discovery) : IAsyncDisposable
{
    private WebApplication? app;
    public PairingService Pairing { get; private set; } = null!;
    public RemoteWebSocketHandler? Handler { get; private set; }
    public string DiscoveryStatus => app?.Services.GetRequiredService<MdnsService>().Status ?? "Stopped";
    public string Status { get; private set; } = "Stopped";
    public async Task StartAsync(IPAddress[]? bindAddresses = null, Action<IServiceCollection>? overrides = null)
    {
        if (app != null) await StopAsync();
        var addresses = bindAddresses ?? discovery.Addresses().Append(IPAddress.Loopback).Distinct().ToArray();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(new FileLoggerProvider(store.DirectoryPath));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 2048;
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.MaxConcurrentUpgradedConnections = 16;
            foreach (var address in addresses) options.Listen(address, store.Current.ServerPort);
            if (store.Current.EnableHttps)
            {
                var certificate = new LocalCertificateService(store, discovery).Load();
                foreach (var address in addresses) options.Listen(address, store.Current.HttpsPort, listen => listen.UseHttps(certificate));
            }
        });
        builder.Services.AddSingleton<IConfigStore>(store);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<IPairingService>(p => p.GetRequiredService<PairingService>());
        builder.Services.AddSingleton<IInputService, InputService>(); builder.Services.AddSingleton<IMouseService, MouseService>();
        builder.Services.AddSingleton<IKeyboardService, KeyboardService>();
        builder.Services.AddSingleton<IVolumeService, VolumeService>(); builder.Services.AddSingleton<IAppLauncherService, AppLauncherService>();
        builder.Services.AddSingleton<IPowerService, PowerService>(); builder.Services.AddSingleton<CommandDispatcher>();
        builder.Services.AddSingleton<RemoteWebSocketHandler>();
        builder.Services.AddSingleton<TextFocusService>();
        builder.Services.AddSingleton<ITextFocusService>(p => p.GetRequiredService<TextFocusService>());
        if (bindAddresses == null) builder.Services.AddHostedService(p => p.GetRequiredService<TextFocusService>());
        builder.Services.AddSingleton(discovery);
        builder.Services.AddSingleton<MdnsService>();
        if (bindAddresses == null) builder.Services.AddHostedService(p => p.GetRequiredService<MdnsService>());
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            // Global pairing limit also bounds attempts from clients cycling LAN IPs.
            options.AddPolicy("pair", _ => RateLimitPartition.GetFixedWindowLimiter("pair", _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        overrides?.Invoke(builder.Services);
        var server = builder.Build();
        var runtimeLogger = server.Services.GetRequiredService<ILogger<ServerRuntime>>();
        var allowedHosts = addresses.Select(a => a.ToString()).Append("localhost").Append("tvpc.local").ToHashSet(StringComparer.OrdinalIgnoreCase);
        server.Use(async (context, next) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            var host = context.Request.Host.Host.Trim('[', ']');
            if (remote == null || !DiscoveryService.IsLan(remote) || !allowedHosts.Contains(host)) { context.Response.StatusCode = 403; return; }
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/ws")
            {
                var origin = context.Request.Headers.Origin.ToString();
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Authority != context.Request.Host.Value || uri.Scheme != context.Request.Scheme)
                { context.Response.StatusCode = 403; return; }
            }
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.CacheControl = "no-store";
            await next();
        });
        server.UseRateLimiter();
        server.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        Pairing = server.Services.GetRequiredService<PairingService>();
        Handler = server.Services.GetRequiredService<RemoteWebSocketHandler>();
        Pairing.Revoked += Handler.Disconnect;
        Pairing.GenerateCode();
        server.MapPost("/api/pair", async (HttpContext context) =>
        {
            try
            {
                if (!context.Request.HasJsonContentType()) return Results.BadRequest(new { message = "Expected JSON." });
                var request = await context.Request.ReadFromJsonAsync<PairRequest>(ConfigStore.Json);
                if (request == null || request.Code == null || request.Name == null) return Results.BadRequest(new { message = "Name and code required." });
                var token = Pairing.Pair(request.Code, request.Name);
                runtimeLogger.LogInformation("Pairing attempt {Result}", token == null ? "rejected" : "accepted");
                return token == null ? Results.Json(new { message = "Code invalid, expired or used. Generate a new code on the PC." }, statusCode: 401) : Results.Json(new { token });
            }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { message = "Invalid pairing request." }); }
        }).RequireRateLimiting("pair");
        server.Map("/ws", Handler.Handle);
        server.MapPost("/api/app-icon/{id}", (string id, HttpContext context, IAppLauncherService apps) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || Pairing.Authenticate(authorization[7..]) == null) return Results.Unauthorized();
            var png = apps.GetIcon(id);
            return png == null ? Results.NotFound() : Results.File(png, "image/png");
        });
        server.MapGet("/TV-Remote-Root.cer", () => File.Exists(Path.Combine(store.DirectoryPath, "TV-Remote-Root.cer"))
            ? Results.File(Path.Combine(store.DirectoryPath, "TV-Remote-Root.cer"), "application/x-x509-ca-cert", "TV-Remote-Root.cer") : Results.NotFound());
        server.UseDefaultFiles(); server.UseStaticFiles();
        try
        {
            await server.StartAsync(); app = server;
            Status = discovery.Addresses().Length == 0 ? "Running — no LAN connection (localhost only)" : "Running";
            runtimeLogger.LogInformation("Server started on {Count} local interfaces, HTTP port {Port}", addresses.Length, store.Current.ServerPort);
        }
        catch (Exception ex) { runtimeLogger.LogError("Server startup failed ({Reason})", ex.GetType().Name); await server.DisposeAsync(); Status = "Failed to start"; throw; }
    }
    public async Task StopAsync()
    {
        if (app != null) { using var timeout = new CancellationTokenSource(3000); try { await app.StopAsync(timeout.Token); } finally { await app.DisposeAsync(); app = null; Handler = null; } }
        Status = "Stopped";
    }
    public async ValueTask DisposeAsync() => await StopAsync();
    private sealed record PairRequest(string Code, string Name);
}
