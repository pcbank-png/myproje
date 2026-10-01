using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public class OfflineLicenseService
    {
        private const string Format = "NSX-LICENSE-OFFLINE-V2";
        private const string Algorithm = "RS256";
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;
        private readonly object _keyLock = new();
        private string? _privateKeyPem;
        private string? _publicKeyPem;

        public OfflineLicenseService(IWebHostEnvironment environment, IConfiguration configuration)
        {
            _environment = environment;
            _configuration = configuration;
        }

        public OfflineLicenseCreateResult CreateCertificate(License license, string productCode, string machineId)
        {
            EnsureKeyPairLoaded();

            var nowUtc = DateTime.UtcNow;
            var certificateId = $"NSX-{Guid.NewGuid():N}".ToUpperInvariant();
            var payload = new OfflineLicensePayload
            {
                Format = Format,
                CertificateVersion = 2,
                CertificateId = certificateId,
                LicenseKeyHash = Sha256(license.LicenseKey),
                LicenseType = license.LicenseType,
                ProductCode = NormalizeProductCode(productCode),
                ProductName = license.Product?.Name ?? "NSX Yazılım Ürünü",
                CustomerName = license.User?.FullName ?? string.Empty,
                CustomerEmail = license.User?.Email ?? string.Empty,
                MachineId = machineId.Trim(),
                StartDateUtc = DateTime.SpecifyKind(license.StartDate, DateTimeKind.Local).ToUniversalTime(),
                EndDateUtc = license.EndDate.HasValue ? DateTime.SpecifyKind(license.EndDate.Value, DateTimeKind.Local).ToUniversalTime() : null,
                IsLifetime = !license.EndDate.HasValue,
                IssuedAtUtc = nowUtc,
                Issuer = "NSX Yazılım",
                KeyId = GetKeyId()
            };

            var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var signature = Sign(payloadJson);
            var envelope = new OfflineLicenseEnvelope
            {
                Format = Format,
                Algorithm = Algorithm,
                KeyId = payload.KeyId,
                PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)),
                Signature = signature,
                PublicKeyPem = _publicKeyPem ?? string.Empty
            };

            var envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var offlineCode = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelopeJson));

            return new OfflineLicenseCreateResult(
                certificateId,
                payloadJson,
                signature,
                offlineCode,
                envelope.PublicKeyPem,
                $"{NormalizeProductCode(productCode)}-{certificateId}.nsxlic");
        }

        public string GetPublicKeyPem()
        {
            EnsureKeyPairLoaded();
            return _publicKeyPem ?? string.Empty;
        }

        private void EnsureKeyPairLoaded()
        {
            if (!string.IsNullOrWhiteSpace(_privateKeyPem) && !string.IsNullOrWhiteSpace(_publicKeyPem))
                return;

            lock (_keyLock)
            {
                if (!string.IsNullOrWhiteSpace(_privateKeyPem) && !string.IsNullOrWhiteSpace(_publicKeyPem))
                    return;

                var configuredPrivateKey = _configuration["LicenseV2:PrivateKeyPem"];
                if (!string.IsNullOrWhiteSpace(configuredPrivateKey))
                {
                    _privateKeyPem = configuredPrivateKey.Replace("\\n", "\n");
                    _publicKeyPem = ExportPublicKey(_privateKeyPem);
                    return;
                }

                var relativeKeyPath = _configuration["LicenseV2:PrivateKeyPath"];
                if (string.IsNullOrWhiteSpace(relativeKeyPath))
                    relativeKeyPath = Path.Combine("App_Data", "license_v2_private_key.pem");

                var keyPath = Path.IsPathRooted(relativeKeyPath)
                    ? relativeKeyPath
                    : Path.Combine(_environment.ContentRootPath, relativeKeyPath);

                Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

                if (!File.Exists(keyPath))
                {
                    using var rsa = RSA.Create(3072);
                    var newPrivateKey = rsa.ExportRSAPrivateKeyPem();
                    File.WriteAllText(keyPath, newPrivateKey, Encoding.UTF8);
                }

                _privateKeyPem = File.ReadAllText(keyPath, Encoding.UTF8);
                _publicKeyPem = ExportPublicKey(_privateKeyPem);
            }
        }

        private string Sign(string payloadJson)
        {
            EnsureKeyPairLoaded();
            using var rsa = RSA.Create();
            rsa.ImportFromPem((_privateKeyPem ?? string.Empty).AsSpan());
            var data = Encoding.UTF8.GetBytes(payloadJson);
            var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(signature);
        }

        private static string ExportPublicKey(string privateKeyPem)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem.AsSpan());
            return rsa.ExportSubjectPublicKeyInfoPem();
        }

        private string GetKeyId()
        {
            var configuredKeyId = _configuration["LicenseV2:KeyId"];
            if (!string.IsNullOrWhiteSpace(configuredKeyId))
                return configuredKeyId.Trim();

            var publicKey = GetPublicKeyPem();
            return "NSX-" + Sha256(publicKey).Substring(0, 12).ToUpperInvariant();
        }

        private static string Sha256(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return Convert.ToHexString(bytes).ToUpperInvariant();
        }

        private static string NormalizeProductCode(string value)
        {
            return new string((value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
        }

        private sealed class OfflineLicensePayload
        {
            public string Format { get; set; } = string.Empty;
            public int CertificateVersion { get; set; }
            public string CertificateId { get; set; } = string.Empty;
            public string LicenseKeyHash { get; set; } = string.Empty;
            public string LicenseType { get; set; } = string.Empty;
            public string ProductCode { get; set; } = string.Empty;
            public string ProductName { get; set; } = string.Empty;
            public string CustomerName { get; set; } = string.Empty;
            public string CustomerEmail { get; set; } = string.Empty;
            public string MachineId { get; set; } = string.Empty;
            public DateTime StartDateUtc { get; set; }
            public DateTime? EndDateUtc { get; set; }
            public bool IsLifetime { get; set; }
            public DateTime IssuedAtUtc { get; set; }
            public string Issuer { get; set; } = string.Empty;
            public string KeyId { get; set; } = string.Empty;
        }

        private sealed class OfflineLicenseEnvelope
        {
            public string Format { get; set; } = string.Empty;
            public string Algorithm { get; set; } = string.Empty;
            public string KeyId { get; set; } = string.Empty;
            public string PayloadBase64 { get; set; } = string.Empty;
            public string Signature { get; set; } = string.Empty;
            public string PublicKeyPem { get; set; } = string.Empty;
        }
    }

    public sealed record OfflineLicenseCreateResult(
        string CertificateId,
        string PayloadJson,
        string Signature,
        string OfflineCode,
        string PublicKeyPem,
        string FileName);
}
