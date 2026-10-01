using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using TvRemote.Configuration;

namespace TvRemote.Services;

public interface IPairingService
{
    (string Code, DateTimeOffset Expires) GenerateCode();
    (string Code, DateTimeOffset Expires) CurrentCode { get; }
    string? Pair(string code, string name);
    PairedDevice? Authenticate(string token);
    void Revoke(string id);
}

public sealed class PairingService(IConfigStore store, TimeProvider clock) : IPairingService
{
    private readonly object gate = new();
    private string code = "";
    private DateTimeOffset expires;
    private int attempts;
    private readonly ConcurrentDictionary<string, byte[]> hashes = new();
    public event Action<string>? Revoked;
    public (string Code, DateTimeOffset Expires) CurrentCode { get { lock (gate) return (code, expires); } }
    public (string Code, DateTimeOffset Expires) GenerateCode()
    {
        lock (gate)
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            expires = clock.GetUtcNow().AddMinutes(5);
            attempts = 0;
            return (code, expires);
        }
    }
    public string? Pair(string supplied, string name)
    {
        lock (gate)
        {
            if (expires <= clock.GetUtcNow() || ++attempts > 10 || supplied.Length != 6 ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(code))) return null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Any(char.IsControl)) return null;
            lock (store.Current) if (store.Current.PairedDevices.Count >= 32) return null;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var device = new PairedDevice { Name = name.Trim(), ProtectedTokenHash = Convert.ToBase64String(ProtectedData.Protect(hash, null, store.ProtectionScope)) };
            lock (store.Current)
            {
                store.Current.PairedDevices.Add(device);
                try { store.Save(); } catch { store.Current.PairedDevices.Remove(device); throw; }
            }
            // One-use codes prevent a second phone from reusing a displayed code.
            expires = clock.GetUtcNow();
            return token;
        }
    }
    public PairedDevice? Authenticate(string token)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        lock (store.Current)
        {
            foreach (var device in store.Current.PairedDevices)
            {
                try
                {
                    var storedHash = hashes.GetOrAdd(device.ProtectedTokenHash, value => ProtectedData.Unprotect(Convert.FromBase64String(value), null, store.ProtectionScope));
                    if (CryptographicOperations.FixedTimeEquals(hash, storedHash)) return device;
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException) { }
            }
        }
        return null;
    }
    public void Revoke(string id)
    {
        lock (store.Current)
        {
            foreach (var device in store.Current.PairedDevices.Where(d => d.Id == id)) hashes.TryRemove(device.ProtectedTokenHash, out _);
            store.Current.PairedDevices.RemoveAll(d => d.Id == id); store.Save();
        }
        Revoked?.Invoke(id);
    }
}
