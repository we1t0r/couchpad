using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RemoteDesk.Agent.Core;

/// <summary>
/// Постоянная «личность» агента: идентификатор и самоподписанный сертификат TLS.
/// Создаются при первом запуске и лежат в папке данных агента.
/// </summary>
public sealed class AgentIdentity
{
    public string Id { get; }
    public string Name { get; }
    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 от DER-сертификата в base64url — его телефон закрепляет при сопряжении.</summary>
    public string Fingerprint { get; }

    AgentIdentity(string id, string name, X509Certificate2 certificate)
    {
        Id = id;
        Name = name;
        Certificate = certificate;
        Fingerprint = FingerprintOf(certificate.RawData);
    }

    public static string FingerprintOf(byte[] derCertificate) =>
        Base64Url.EncodeToString(SHA256.HashData(derCertificate));

    public static AgentIdentity LoadOrCreate(string dataDir, string? name = null)
    {
        Directory.CreateDirectory(dataDir);
        var idPath = Path.Combine(dataDir, "agent.json");
        var pfxPath = Path.Combine(dataDir, "agent.pfx");

        string id;
        if (File.Exists(idPath))
        {
            id = JsonSerializer.Deserialize<IdFile>(File.ReadAllText(idPath), Message.Options)?.Id
                 ?? throw new InvalidDataException($"Повреждён {idPath}");
        }
        else
        {
            id = Guid.NewGuid().ToString("N");
            File.WriteAllText(idPath, JsonSerializer.Serialize(new IdFile(id), Message.Options));
        }

        if (!File.Exists(pfxPath))
            File.WriteAllBytes(pfxPath, CreateCertificate());

        // Загружаем из файла, а не используем только что созданный объект:
        // у него ключ «эфемерный», и TLS в Windows (SChannel) с таким не работает.
        var cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
        return new AgentIdentity(id, name ?? Environment.MachineName, cert);
    }

    static byte[] CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Couchpad Agent", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], false));
        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        return cert.Export(X509ContentType.Pfx);
    }

    sealed record IdFile(string Id);
}
