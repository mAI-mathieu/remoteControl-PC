using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TvRemote.Configuration;

namespace TvRemote.Services;
public sealed class LocalCertificateService(ConfigStore store, DiscoveryService discovery)
{
    public string RootPath => Path.Combine(store.DirectoryPath, "TV-Remote-Root.cer");
    public string PfxPath => Path.Combine(store.DirectoryPath, "server.dpapi");
    public X509Certificate2 Load()
    {
        if (!File.Exists(PfxPath)) Generate();
        return X509CertificateLoader.LoadPkcs12(ProtectedData.Unprotect(File.ReadAllBytes(PfxPath), null, DataProtectionScope.CurrentUser), null);
    }
    public void Generate()
    {
        using var rootKey = RSA.Create(3072);
        var rootRequest = new CertificateRequest("CN=TV Remote Local Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=tvpc.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("tvpc.local"); san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        foreach (var address in discovery.Addresses()) san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        using var signed = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365), RandomNumberGenerator.GetBytes(16));
        using var server = signed.CopyWithPrivateKey(key);
        using var publicRoot = X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert));
        var chain = new X509Certificate2Collection { server, publicRoot };
        File.WriteAllBytes(PfxPath, ProtectedData.Protect(chain.Export(X509ContentType.Pfx)!, null, DataProtectionScope.CurrentUser));
        File.WriteAllBytes(RootPath, root.Export(X509ContentType.Cert));
    }
}
