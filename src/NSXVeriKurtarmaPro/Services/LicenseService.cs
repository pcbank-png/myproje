using Microsoft.Win32;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NSXVeriKurtarmaPro.Services;

public sealed class LicenseStatus
{
    public bool IsLicensed { get; init; }
    public bool IsPendingOnlineVerification { get; init; }
    public bool IsOffline { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string LicenseKind { get; init; } = string.Empty;
    public DateTime? ExpireDate { get; init; }
    public DateTime? LastOnlineCheckAt { get; init; }
}

public sealed class LicenseCheckResult
{
    public bool Success { get; init; }
    public bool ApiUnavailable { get; init; }
    public bool ExplicitRejected { get; init; }
    public bool IsOfflineAccepted { get; init; }
    public bool IsPendingOffline { get; init; }
    public bool? IsLifetime { get; init; }
    public DateTime? StartDate { get; init; }
    public int? RemainingDays { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime? ExpireDate { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string LicenseKey { get; init; } = string.Empty;
    public string LicenseKind { get; init; } = string.Empty;
    public string LicenseMode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public static class LicenseService
{
    public const string ProductCode = "NSXDATARECOVERYPRO";
    public const string ProductName = "NSX Veri Kurtarma Pro";

    private const string ProductSlug = "nsx-veri-kurtarma-pro";
    private const string DefaultLicenseApiKey = "NSX-LIC-V2-7F9D2A6C8B4E4F1A9C0E5D3B7A1F6E92";
    private const string LegacyLicenseApiKey = "NS-SECRET-2026";
    private const int UnknownDurationOnlineGraceDays = 2;

    private const string ModeOnlineVerified = "OnlineVerified";
    private const string ModeOfflineCached = "OfflineCached";
    private const string ModeOfflinePending = "OfflinePending";
    private const string ModeOnlineRejected = "OnlineRejected";

    private static readonly string[] ActivateV2Urls =
    {
        "https://www.nsxyazilim.com/api/license/v2/activate",
        "https://nsxyazilim.com/api/license/v2/activate",
        "https://www.nsxyazilim.com/api/License/v2/Activate",
        "https://nsxyazilim.com/api/License/v2/Activate",
        "https://www.nsxyazilim.com/api/license/v2/check",
        "https://nsxyazilim.com/api/license/v2/check",
        "https://www.nsxyazilim.com/api/License/v2/Check",
        "https://nsxyazilim.com/api/License/v2/Check"
    };

    private static readonly string[] LegacyActivateUrls =
    {
        "https://www.nsxyazilim.com/License/Activate",
        "https://nsxyazilim.com/License/Activate",
        "https://www.nsxyazilim.com/License/Status",
        "https://nsxyazilim.com/License/Status"
    };

    private static readonly SemaphoreSlim StateGate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static LicenseStatus GetStatus()
    {
        PersistedLicenseState state = LoadState();
        DateTime now = GetEffectiveUtcNow(state);

        if (IsStoredLicenseValid(state, now))
        {
            bool offline = string.Equals(state.LicenseMode, ModeOfflineCached, StringComparison.OrdinalIgnoreCase);
            return new LicenseStatus
            {
                IsLicensed = true,
                IsPendingOnlineVerification = false,
                IsOffline = offline,
                Message = BuildLicensedStatusMessage(state, now),
                Email = state.Email ?? string.Empty,
                LicenseKind = NormalizeLicenseKindForDisplay(state.LicenseType),
                ExpireDate = state.ExpireDateUtc?.ToLocalTime(),
                LastOnlineCheckAt = state.LastOnlineCheckUtc?.ToLocalTime()
            };
        }

        bool pending = HasStoredCredentials(state) &&
                       string.Equals(state.LicenseMode, ModeOfflinePending, StringComparison.OrdinalIgnoreCase);
        bool rejected = HasStoredCredentials(state) &&
                        string.Equals(state.LicenseMode, ModeOnlineRejected, StringComparison.OrdinalIgnoreCase);

        string message = pending
            ? "Online doğrulama bekleniyor. Lisans doğrulanana kadar tarama ve önizleme kullanılabilir; dosya kurtarma kilitlidir."
            : rejected
                ? "Lisans doğrulanamadı. Tarama ve önizleme kullanılabilir; dosya kurtarma için geçerli bir lisans gerekir."
                : "Lisans gerekli. Tarama ve önizleme kullanılabilir; dosya kurtarma için lisans aktivasyonu gerekir.";

        return new LicenseStatus
        {
            IsLicensed = false,
            IsPendingOnlineVerification = pending,
            IsOffline = pending,
            Message = message,
            Email = state.Email ?? string.Empty,
            LicenseKind = "Lisanssız",
            LastOnlineCheckAt = state.LastOnlineCheckUtc?.ToLocalTime()
        };
    }

    public static (string Email, string LicenseKey) GetStoredCredentials()
    {
        PersistedLicenseState state = LoadState();
        return (state.Email?.Trim() ?? string.Empty, state.LicenseKey?.Trim() ?? string.Empty);
    }

    public static bool HasStoredLicenseCredentials()
    {
        PersistedLicenseState state = LoadState();
        return HasStoredCredentials(state);
    }

    public static string GetMachineId()
    {
        string rawMachineId = string.Empty;

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            rawMachineId = key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? string.Empty;
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(rawMachineId))
            rawMachineId = Environment.MachineName;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"NSX|DATARECOVERY|{rawMachineId}"));
        return Convert.ToHexString(hash);
    }

    public static async Task<LicenseCheckResult> ActivateOnlineAsync(string email, string licenseKey)
    {
        string cleanEmail = (email ?? string.Empty).Trim();
        string cleanKey = (licenseKey ?? string.Empty).Trim().ToUpperInvariant();

        if (!LooksLikeEmail(cleanEmail))
        {
            return new LicenseCheckResult
            {
                Success = false,
                ExplicitRejected = true,
                Message = "Lütfen geçerli bir e-posta adresi girin."
            };
        }

        if (string.IsNullOrWhiteSpace(cleanKey))
        {
            return new LicenseCheckResult
            {
                Success = false,
                ExplicitRejected = true,
                Message = "Lütfen lisans anahtarınızı girin."
            };
        }

        LicenseCheckResult onlineResult = await TryActivateOnlineV2Async(cleanEmail, cleanKey);
        if (onlineResult.Success)
        {
            await SaveActivatedLicenseAsync(cleanEmail, cleanKey, onlineResult);
            return onlineResult;
        }

        if (onlineResult.ApiUnavailable)
        {
            await SavePendingCredentialsAsync(cleanEmail, cleanKey, onlineResult.Message);
            return new LicenseCheckResult
            {
                Success = false,
                ApiUnavailable = true,
                IsPendingOffline = true,
                ProductCode = ProductCode,
                ProductName = ProductName,
                LicenseKey = cleanKey,
                LicenseMode = ModeOfflinePending,
                Status = "PendingOnlineVerification",
                Message = "Lisans sunucusuna şu anda ulaşılamıyor. Bilgiler güvenli biçimde kaydedildi; bağlantı geldiğinde yeniden doğrulayabilirsiniz."
            };
        }

        if (onlineResult.ExplicitRejected)
            await SaveRejectedCredentialsAsync(cleanEmail, cleanKey, onlineResult);

        return onlineResult;
    }

    public static async Task<LicenseCheckResult> RefreshStoredLicenseOnlineAsync()
    {
        PersistedLicenseState current = LoadState();
        if (!HasStoredCredentials(current))
        {
            return new LicenseCheckResult
            {
                Success = false,
                Message = "Online kontrol için kayıtlı lisans bilgisi bulunamadı."
            };
        }

        string email = current.Email!.Trim();
        string licenseKey = current.LicenseKey!.Trim().ToUpperInvariant();
        LicenseCheckResult result = await TryActivateOnlineV2Async(email, licenseKey);

        await StateGate.WaitAsync();
        try
        {
            PersistedLicenseState state = LoadStateCore();
            state.LastOnlineCheckUtc = DateTime.UtcNow;
            state.LastOnlineResult = Shorten(result.Message, 450);

            if (result.Success)
            {
                ApplyOnlineVerifiedValues(state, email, licenseKey, result);
            }
            else if (result.ApiUnavailable)
            {
                DateTime now = GetEffectiveUtcNow(state);
                if (IsStoredLicenseValid(state, now))
                {
                    state.LicenseMode = ModeOfflineCached;
                    state.IsActive = true;
                }
                else
                {
                    state.Email = email;
                    state.LicenseKey = licenseKey;
                    state.ProductCode = ProductCode;
                    state.MachineId = GetMachineId();
                    state.LicenseMode = ModeOfflinePending;
                    state.LicenseType = "Unlicensed - Online doğrulama bekleniyor";
                    state.IsActive = false;
                    state.ActivationDateUtc = null;
                    state.ExpireDateUtc = null;
                }
            }
            else if (result.ExplicitRejected)
            {
                ApplyRejectedValues(state, email, licenseKey, result);
            }

            SaveStateCore(state);
        }
        finally
        {
            StateGate.Release();
        }

        return result;
    }

    public static void ClearStoredLicense()
    {
        StateGate.Wait();
        try
        {
            PersistedLicenseState state = LoadStateCore();
            state.Email = null;
            state.LicenseKey = null;
            state.ProductCode = ProductCode;
            state.MachineId = GetMachineId();
            state.LicenseType = "Unlicensed";
            state.LicenseMode = null;
            state.IsActive = false;
            state.ActivationDateUtc = null;
            state.ExpireDateUtc = null;
            state.LastOnlineCheckUtc = DateTime.UtcNow;
            state.LastOnlineResult = "Lisans kullanıcı tarafından iptal edildi.";
            SaveStateCore(state);
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static async Task<LicenseCheckResult> TryActivateOnlineV2Async(string email, string licenseKey)
    {
        LicenseCheckResult? lastUnavailable = null;

        foreach (string endpoint in ActivateV2Urls)
        {
            LicenseCheckResult result = await TryActivateOnlineV2EndpointAsync(email, licenseKey, endpoint);
            if (result.Success || result.ExplicitRejected)
                return result;

            if (result.ApiUnavailable)
                lastUnavailable = result;
        }

        LicenseCheckResult legacy = await TryActivateLegacyAsync(email, licenseKey);
        if (legacy.Success || legacy.ExplicitRejected)
            return legacy;

        return lastUnavailable ?? legacy;
    }

    private static async Task<LicenseCheckResult> TryActivateOnlineV2EndpointAsync(
        string email,
        string licenseKey,
        string endpointUrl)
    {
        LicenseCheckResult? lastAuthorizationProblem = null;

        foreach (string apiKey in GetLicenseApiKeyCandidates())
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"NSX-VeriKurtarmaPro/{GetAppVersion()}");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);

                var payload = new
                {
                    Email = email,
                    CustomerEmail = email,
                    LicenseKey = licenseKey,
                    ProductCode,
                    ProductName,
                    ProductSlug,
                    MachineId = GetMachineId(),
                    DeviceName = Environment.MachineName,
                    AppVersion = GetAppVersion(),
                    OsVersion = Environment.OSVersion.VersionString,
                    ApiKey = apiKey
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await client.PostAsync(endpointUrl, content);
                string json = await response.Content.ReadAsStringAsync();

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    lastAuthorizationProblem = BuildUnavailable(
                        ProductCode,
                        "Lisans API anahtarı doğrulanamadı.",
                        response.StatusCode.ToString());
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return BuildUnavailable(ProductCode, "V2 lisans endpoint'i bulunamadı.", "RouteNotFound");

                if (string.IsNullOrWhiteSpace(json))
                    return BuildUnavailable(ProductCode, $"Lisans API boş cevap döndürdü. HTTP {(int)response.StatusCode}.");

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(json);
                }
                catch
                {
                    return BuildUnavailable(ProductCode, $"Lisans API okunabilir JSON cevap döndürmedi. HTTP {(int)response.StatusCode}.");
                }

                using (document)
                {
                    JsonElement root = GetResponsePayload(document.RootElement);
                    string serverMessage = ReadString(root, "message", "Message", "error", "Error", "reason", "Reason", "detail", "Detail", "title", "Title");

                    if (IsApiAuthorizationProblem(response.StatusCode, serverMessage))
                    {
                        lastAuthorizationProblem = BuildUnavailable(ProductCode, "Lisans API anahtarı doğrulanamadı.", "Unauthorized");
                        continue;
                    }

                    if (!response.IsSuccessStatusCode &&
                        response.StatusCode != HttpStatusCode.BadRequest &&
                        response.StatusCode != HttpStatusCode.Conflict)
                    {
                        return BuildUnavailable(
                            ProductCode,
                            string.IsNullOrWhiteSpace(serverMessage)
                                ? $"Lisans API geçici hata döndürdü: {(int)response.StatusCode}."
                                : serverMessage,
                            response.StatusCode.ToString());
                    }

                    return ParseActivationResponse(root, response.IsSuccessStatusCode, email, licenseKey, ProductCode, serverMessage);
                }
            }
            catch (HttpRequestException ex)
            {
                return BuildUnavailable(ProductCode, "Lisans API bağlantısı kurulamadı: " + ex.Message);
            }
            catch (TaskCanceledException)
            {
                return BuildUnavailable(ProductCode, "Lisans API zaman aşımına uğradı.");
            }
            catch (Exception ex)
            {
                return BuildUnavailable(ProductCode, "Lisans API kontrolü yapılamadı: " + ex.Message);
            }
        }

        return lastAuthorizationProblem ?? BuildUnavailable(ProductCode, "Lisans API anahtarı doğrulanamadı.", "Unauthorized");
    }

    private static async Task<LicenseCheckResult> TryActivateLegacyAsync(string email, string licenseKey)
    {
        LicenseCheckResult? lastUnavailable = null;

        foreach (string endpoint in LegacyActivateUrls)
        {
            foreach (string apiKey in GetLicenseApiKeyCandidates())
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"NSX-VeriKurtarmaPro/{GetAppVersion()}");
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);

                    string url = endpoint +
                                 "?key=" + Uri.EscapeDataString(licenseKey) +
                                 "&licenseKey=" + Uri.EscapeDataString(licenseKey) +
                                 "&machineId=" + Uri.EscapeDataString(GetMachineId()) +
                                 "&email=" + Uri.EscapeDataString(email) +
                                 "&customerEmail=" + Uri.EscapeDataString(email) +
                                 "&productCode=" + Uri.EscapeDataString(ProductCode) +
                                 "&urunKodu=" + Uri.EscapeDataString(ProductCode) +
                                 "&apiKey=" + Uri.EscapeDataString(apiKey);

                    using HttpResponseMessage response = await client.GetAsync(url);
                    string json = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        lastUnavailable = BuildUnavailable(ProductCode, "Lisans API anahtarı doğrulanamadı.", "Unauthorized");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        lastUnavailable = BuildUnavailable(ProductCode, $"Eski lisans API boş cevap döndürdü. HTTP {(int)response.StatusCode}.");
                        continue;
                    }

                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(json);
                    }
                    catch
                    {
                        lastUnavailable = BuildUnavailable(ProductCode, $"Eski lisans API okunabilir JSON cevap döndürmedi. HTTP {(int)response.StatusCode}.");
                        continue;
                    }

                    using (document)
                    {
                        JsonElement root = GetResponsePayload(document.RootElement);
                        string serverMessage = ReadString(root, "message", "Message", "error", "Error", "reason", "Reason", "detail", "Detail");

                        if (IsApiAuthorizationProblem(response.StatusCode, serverMessage))
                        {
                            lastUnavailable = BuildUnavailable(ProductCode, "Lisans API anahtarı doğrulanamadı.", "Unauthorized");
                            continue;
                        }

                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            string normalized = NormalizeText(serverMessage);
                            bool licenseReallyMissing = normalized.Contains("LISANS", StringComparison.OrdinalIgnoreCase) &&
                                                        normalized.Contains("BULUNAMADI", StringComparison.OrdinalIgnoreCase);
                            if (!licenseReallyMissing)
                            {
                                lastUnavailable = BuildUnavailable(ProductCode, "Eski lisans endpoint'i bulunamadı.", "RouteNotFound");
                                continue;
                            }
                        }

                        if (!response.IsSuccessStatusCode &&
                            response.StatusCode != HttpStatusCode.BadRequest &&
                            response.StatusCode != HttpStatusCode.Conflict &&
                            response.StatusCode != HttpStatusCode.NotFound)
                        {
                            lastUnavailable = BuildUnavailable(
                                ProductCode,
                                string.IsNullOrWhiteSpace(serverMessage)
                                    ? $"Eski lisans API geçici hata döndürdü: {(int)response.StatusCode}."
                                    : serverMessage,
                                response.StatusCode.ToString());
                            continue;
                        }

                        return ParseActivationResponse(root, response.IsSuccessStatusCode, email, licenseKey, ProductCode, serverMessage);
                    }
                }
                catch (HttpRequestException ex)
                {
                    lastUnavailable = BuildUnavailable(ProductCode, "Lisans API bağlantısı kurulamadı: " + ex.Message);
                }
                catch (TaskCanceledException)
                {
                    lastUnavailable = BuildUnavailable(ProductCode, "Lisans API zaman aşımına uğradı.");
                }
                catch (Exception ex)
                {
                    lastUnavailable = BuildUnavailable(ProductCode, "Lisans API kontrolü yapılamadı: " + ex.Message);
                }
            }
        }

        return lastUnavailable ?? BuildUnavailable(ProductCode, "Lisans sunucusuna ulaşılamadı.");
    }

    private static LicenseCheckResult ParseActivationResponse(
        JsonElement root,
        bool httpSuccess,
        string requestedEmail,
        string requestedKey,
        string requestedProductCode,
        string serverMessage)
    {
        bool? successFlag = ReadNullableBool(root, "success", "Success", "ok", "Ok", "activated", "Activated", "isValid", "IsValid");
        bool? allowed = ReadNullableBool(root, "allowed", "Allowed");
        bool? active = ReadNullableBool(root, "active", "Active", "isActive", "IsActive");
        bool? valid = ReadNullableBool(root, "valid", "Valid", "isValid", "IsValid");
        bool success = successFlag ?? active ?? valid ?? httpSuccess;

        if (allowed == false || active == false || valid == false)
            success = false;

        string status = ReadString(root, "status", "Status", "licenseStatus", "LicenseStatus", "code", "Code");
        string responseProductCode = ReadString(root, "productCode", "ProductCode", "product_code", "Product_Code");
        string acceptedProductCode = string.IsNullOrWhiteSpace(responseProductCode)
            ? requestedProductCode
            : NormalizeProductCode(responseProductCode);
        string productName = ReadString(root, "productName", "ProductName", "product", "Product", "productTitle", "ProductTitle", "appName", "AppName");
        string returnedEmail = ReadString(root, "customerEmail", "CustomerEmail", "email", "Email", "requestEmail", "RequestEmail");
        string returnedKey = ReadString(root, "licenseKey", "LicenseKey", "key", "Key");
        string licenseKind = ReadString(root, "licenseType", "LicenseType", "licenseKind", "LicenseKind", "type", "Type", "plan", "Plan", "period", "Period", "duration", "Duration");
        DateTime? startDate = ReadDate(root, "startDate", "StartDate", "validFrom", "ValidFrom", "activationDate", "ActivationDate");
        DateTime? expireDate = ReadDate(root, "expireDate", "ExpireDate", "expiryDate", "ExpiryDate", "endDate", "EndDate", "validUntil", "ValidUntil");
        int? remainingDays = ReadNullableInt(root, "remainingDays", "RemainingDays", "daysLeft", "DaysLeft", "daysRemaining", "DaysRemaining");
        bool? serverLifetime = ReadNullableBool(root, "isLifetime", "IsLifetime", "lifetime", "Lifetime", "isUnlimited", "IsUnlimited", "unlimited", "Unlimited");

        if (string.IsNullOrWhiteSpace(returnedKey))
            returnedKey = requestedKey;

        if (!success)
        {
            return new LicenseCheckResult
            {
                Success = false,
                ExplicitRejected = true,
                ProductCode = acceptedProductCode,
                ProductName = productName,
                LicenseKey = returnedKey,
                LicenseKind = licenseKind,
                StartDate = startDate,
                ExpireDate = expireDate,
                RemainingDays = remainingDays,
                LicenseMode = ModeOnlineRejected,
                Status = string.IsNullOrWhiteSpace(status) ? "Rejected" : status,
                Message = BuildRejectedMessage(status, serverMessage)
            };
        }

        if (!string.IsNullOrWhiteSpace(returnedEmail) &&
            !string.Equals(returnedEmail.Trim(), requestedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new LicenseCheckResult
            {
                Success = false,
                ExplicitRejected = true,
                ProductCode = acceptedProductCode,
                ProductName = productName,
                LicenseKey = returnedKey,
                LicenseMode = ModeOnlineRejected,
                Status = "EmailMismatch",
                Message = "E-posta ile lisans anahtarı eşleşmiyor."
            };
        }

        if (!string.Equals(NormalizeProductCode(acceptedProductCode), NormalizeProductCode(ProductCode), StringComparison.OrdinalIgnoreCase))
        {
            return new LicenseCheckResult
            {
                Success = false,
                ExplicitRejected = true,
                ProductCode = acceptedProductCode,
                ProductName = productName,
                LicenseKey = returnedKey,
                LicenseMode = ModeOnlineRejected,
                Status = "ProductMismatch",
                Message = "Bu lisans NSX Veri Kurtarma Pro için geçerli değildir."
            };
        }

        bool timedEvidence = expireDate.HasValue ||
                             remainingDays.HasValue ||
                             serverLifetime == false ||
                             IsTimedLicense(licenseKind);
        bool lifetimeEvidence = serverLifetime == true || IsLifetimeLicense(licenseKind);
        bool isLifetime = !timedEvidence && lifetimeEvidence;
        bool usedSafeGrace = false;

        if (!isLifetime)
        {
            expireDate = ResolveTimedExpireDate(expireDate, startDate, remainingDays, licenseKind);
            if (!expireDate.HasValue)
            {
                expireDate = DateTime.UtcNow.Date.AddDays(UnknownDurationOnlineGraceDays);
                usedSafeGrace = true;
            }

            if (expireDate.Value.ToUniversalTime().Date < DateTime.UtcNow.Date)
            {
                return new LicenseCheckResult
                {
                    Success = false,
                    ExplicitRejected = true,
                    ProductCode = ProductCode,
                    ProductName = string.IsNullOrWhiteSpace(productName) ? ProductName : productName,
                    LicenseKey = returnedKey,
                    LicenseKind = licenseKind,
                    StartDate = startDate,
                    ExpireDate = expireDate,
                    RemainingDays = 0,
                    IsLifetime = false,
                    LicenseMode = ModeOnlineRejected,
                    Status = "Expired",
                    Message = "Lisans süresi dolmuş."
                };
            }
        }
        else
        {
            expireDate = null;
        }

        int? calculatedRemaining = isLifetime
            ? null
            : Math.Max(0, (expireDate!.Value.ToUniversalTime().Date - DateTime.UtcNow.Date).Days);

        string successMessage = isLifetime
            ? "Lisans online olarak doğrulandı. Ömür boyu lisans aktif."
            : usedSafeGrace
                ? $"Lisans online olarak doğrulandı. Sunucu süre bilgisini eksik gönderdi; {calculatedRemaining} günlük güvenli online kontrol süresiyle aktif edildi."
                : $"Lisans online olarak doğrulandı. {calculatedRemaining} gün kaldı.";

        return new LicenseCheckResult
        {
            Success = true,
            IsLifetime = isLifetime,
            Message = successMessage,
            StartDate = startDate,
            ExpireDate = expireDate,
            RemainingDays = calculatedRemaining,
            ProductCode = ProductCode,
            ProductName = string.IsNullOrWhiteSpace(productName) ? ProductName : productName,
            LicenseKey = returnedKey,
            LicenseKind = isLifetime ? "Lifetime" : string.IsNullOrWhiteSpace(licenseKind) ? "Yearly" : licenseKind.Trim(),
            LicenseMode = ModeOnlineVerified,
            Status = string.IsNullOrWhiteSpace(status) ? "OnlineVerified" : status
        };
    }

    private static string BuildRejectedMessage(string status, string serverMessage)
    {
        string normalized = NormalizeText($"{status} {serverMessage}");

        if (normalized.Contains("EMAIL", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("E-POSTA", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("EPOSTA", StringComparison.OrdinalIgnoreCase))
        {
            return "E-posta ile lisans anahtarı eşleşmiyor.";
        }

        if (normalized.Contains("MACHINE", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("DEVICE", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("CIHAZ", StringComparison.OrdinalIgnoreCase))
        {
            return "Lisans bu cihaz için kullanılamıyor. Cihaz eşleştirmesini kontrol edin.";
        }

        if (normalized.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("SURESI DOLDU", StringComparison.OrdinalIgnoreCase))
        {
            return "Lisans süresi dolmuş.";
        }

        if (normalized.Contains("PRODUCT", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("URUN", StringComparison.OrdinalIgnoreCase))
        {
            return "Bu lisans NSX Veri Kurtarma Pro için geçerli değildir.";
        }

        // Sunucudan gelen ham hata metnini arayüze taşımıyoruz. Böylece hem
        // dil sızıntısı hem de API iç ayrıntılarının kullanıcıya görünmesi engellenir.
        return "Lisans doğrulanamadı. E-posta adresi ve lisans anahtarını kontrol edin.";
    }

    private static async Task SaveActivatedLicenseAsync(string email, string licenseKey, LicenseCheckResult result)
    {
        await StateGate.WaitAsync();
        try
        {
            PersistedLicenseState state = LoadStateCore();
            ApplyOnlineVerifiedValues(state, email, licenseKey, result);
            SaveStateCore(state);
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static void ApplyOnlineVerifiedValues(
        PersistedLicenseState state,
        string email,
        string licenseKey,
        LicenseCheckResult result)
    {
        state.Email = email.Trim();
        state.LicenseKey = licenseKey.Trim().ToUpperInvariant();
        state.ProductCode = ProductCode;
        state.MachineId = GetMachineId();
        state.LicenseType = string.IsNullOrWhiteSpace(result.LicenseKind)
            ? result.IsLifetime == true ? "Lifetime" : "Yearly"
            : result.LicenseKind.Trim();
        state.LicenseMode = ModeOnlineVerified;
        state.IsActive = true;
        state.ActivationDateUtc = (result.StartDate ?? DateTime.Now).ToUniversalTime();
        state.ExpireDateUtc = result.IsLifetime == true || !result.ExpireDate.HasValue
            ? null
            : result.ExpireDate.Value.ToUniversalTime();
        state.LastOnlineCheckUtc = DateTime.UtcNow;
        state.LastOnlineResult = Shorten(result.Message, 450);
    }

    private static async Task SavePendingCredentialsAsync(string email, string licenseKey, string reason)
    {
        await StateGate.WaitAsync();
        try
        {
            PersistedLicenseState state = LoadStateCore();
            DateTime now = GetEffectiveUtcNow(state);

            if (IsStoredLicenseValid(state, now))
            {
                state.LastOnlineCheckUtc = DateTime.UtcNow;
                state.LastOnlineResult = Shorten(reason, 450);
                state.LicenseMode = ModeOfflineCached;
                SaveStateCore(state);
                return;
            }

            state.Email = email.Trim();
            state.LicenseKey = licenseKey.Trim().ToUpperInvariant();
            state.ProductCode = ProductCode;
            state.MachineId = GetMachineId();
            state.LicenseType = "Unlicensed - Online doğrulama bekleniyor";
            state.LicenseMode = ModeOfflinePending;
            state.IsActive = false;
            state.ActivationDateUtc = null;
            state.ExpireDateUtc = null;
            state.LastOnlineCheckUtc = DateTime.UtcNow;
            state.LastOnlineResult = Shorten(reason, 450);
            SaveStateCore(state);
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static async Task SaveRejectedCredentialsAsync(string email, string licenseKey, LicenseCheckResult result)
    {
        await StateGate.WaitAsync();
        try
        {
            PersistedLicenseState state = LoadStateCore();
            ApplyRejectedValues(state, email, licenseKey, result);
            SaveStateCore(state);
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static void ApplyRejectedValues(
        PersistedLicenseState state,
        string email,
        string licenseKey,
        LicenseCheckResult result)
    {
        state.Email = email.Trim();
        state.LicenseKey = licenseKey.Trim().ToUpperInvariant();
        state.ProductCode = ProductCode;
        state.MachineId = GetMachineId();
        state.LicenseType = "Rejected";
        state.LicenseMode = ModeOnlineRejected;
        state.IsActive = false;
        state.ActivationDateUtc = null;
        state.ExpireDateUtc = null;
        state.LastOnlineCheckUtc = DateTime.UtcNow;
        state.LastOnlineResult = Shorten(result.Message, 450);
    }

    private static bool IsStoredLicenseValid(PersistedLicenseState state, DateTime nowUtc)
    {
        if (!state.IsActive || string.IsNullOrWhiteSpace(state.LicenseKey))
            return false;

        if (!string.Equals(NormalizeProductCode(state.ProductCode), NormalizeProductCode(ProductCode), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(state.MachineId, GetMachineId(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (state.ExpireDateUtc.HasValue && state.ExpireDateUtc.Value.Date < nowUtc.Date)
            return false;

        if (!state.ExpireDateUtc.HasValue && !IsLifetimeLicense(state.LicenseType))
            return false;

        return true;
    }

    private static string BuildLicensedStatusMessage(PersistedLicenseState state, DateTime nowUtc)
    {
        bool offline = string.Equals(state.LicenseMode, ModeOfflineCached, StringComparison.OrdinalIgnoreCase);
        bool lifetime = !state.ExpireDateUtc.HasValue && IsLifetimeLicense(state.LicenseType);

        if (lifetime)
            return offline ? "Lisans aktif - Çevrimdışı (Ömür Boyu)." : "Lisans aktif - Ömür Boyu.";

        if (state.ExpireDateUtc.HasValue)
        {
            int remainingDays = Math.Max(0, (state.ExpireDateUtc.Value.Date - nowUtc.Date).Days);
            return offline
                ? $"Lisans aktif - Çevrimdışı ({remainingDays} gün kaldı)."
                : $"Lisans aktif - {remainingDays} gün kaldı.";
        }

        return offline ? "Lisans aktif - Çevrimdışı." : "Lisans aktif.";
    }

    private static string NormalizeLicenseKindForDisplay(string? value)
    {
        if (IsLifetimeLicense(value))
            return "Ömür Boyu";
        if (IsTimedLicense(value))
            return "Süreli";
        return string.IsNullOrWhiteSpace(value) ? "Lisanslı" : value.Trim();
    }

    private static DateTime? ResolveTimedExpireDate(
        DateTime? expireDate,
        DateTime? startDate,
        int? remainingDays,
        string? licenseKind)
    {
        if (expireDate.HasValue)
            return expireDate.Value;

        if (remainingDays.HasValue)
            return DateTime.UtcNow.Date.AddDays(Math.Max(0, remainingDays.Value));

        if (!startDate.HasValue)
            return null;

        string normalized = NormalizeText(licenseKind ?? string.Empty);
        if (normalized.Contains("MONTH", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("AYLIK", StringComparison.OrdinalIgnoreCase))
        {
            return startDate.Value.AddMonths(1);
        }

        return startDate.Value.AddYears(1);
    }

    private static bool IsLifetimeLicense(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = NormalizeText(value);
        return normalized.Contains("OMUR", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("SINIRSIZ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("SURESIZ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("LIFETIME", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("PERMANENT", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("UNLIMITED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTimedLicense(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = NormalizeText(value);
        return normalized.Contains("YEAR", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("YILLIK", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("ANNUAL", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("SURELI", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("SUBSCRIPTION", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("MONTH", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("AYLIK", StringComparison.OrdinalIgnoreCase);
    }

    private static LicenseCheckResult BuildUnavailable(string productCode, string message, string status = "Unavailable") =>
        new()
        {
            Success = false,
            ApiUnavailable = true,
            ExplicitRejected = false,
            ProductCode = productCode,
            ProductName = ProductName,
            Status = status,
            Message = message
        };

    private static string[] GetLicenseApiKeyCandidates()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("NSX_LICENSE_API_KEY");
        return new[] { fromEnvironment, DefaultLicenseApiKey, LegacyLicenseApiKey }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static JsonElement GetResponsePayload(JsonElement root)
    {
        foreach (string name in new[] { "data", "Data", "result", "Result", "license", "License" })
        {
            if (root.TryGetProperty(name, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
                return nested;
        }

        return root;
    }

    private static int? ReadNullableInt(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int number))
                return number;

            if (int.TryParse(property.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
        }

        return null;
    }

    private static bool IsApiAuthorizationProblem(HttpStatusCode statusCode, string? message)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return true;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        string normalized = NormalizeText(message);
        return (normalized.Contains("API", StringComparison.OrdinalIgnoreCase) &&
                (normalized.Contains("ANAHTAR", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("KEY", StringComparison.OrdinalIgnoreCase))) ||
               normalized.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("FORBIDDEN", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ReadNullableBool(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                continue;

            if (property.ValueKind == JsonValueKind.True)
                return true;
            if (property.ValueKind == JsonValueKind.False)
                return false;
            if (bool.TryParse(property.ToString(), out bool parsed))
                return parsed;
            if (int.TryParse(property.ToString(), out int number))
                return number != 0;
        }

        return null;
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                continue;

            string? value = property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static DateTime? ReadDate(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                continue;

            if (property.ValueKind == JsonValueKind.String && property.TryGetDateTime(out DateTime parsedDate))
                return parsedDate;

            if (DateTime.TryParse(property.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsedDate) ||
                DateTime.TryParse(property.ToString(), out parsedDate))
            {
                return parsedDate;
            }
        }

        return null;
    }

    private static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            return false;

        int at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at >= value.Length - 3)
            return false;

        int dot = value.IndexOf('.', at + 2);
        return dot > at + 1 && dot < value.Length - 1;
    }

    private static string NormalizeProductCode(string? value) =>
        new((value ?? string.Empty)
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Trim().ToUpperInvariant()
            .Replace('İ', 'I')
            .Replace('Ş', 'S')
            .Replace('Ğ', 'G')
            .Replace('Ü', 'U')
            .Replace('Ö', 'O')
            .Replace('Ç', 'C');

        return normalized;
    }

    private static string Shorten(string? value, int maxLength)
    {
        string text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string GetAppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.3.0";

    private static bool HasStoredCredentials(PersistedLicenseState state) =>
        !string.IsNullOrWhiteSpace(state.Email) && !string.IsNullOrWhiteSpace(state.LicenseKey);

    private static PersistedLicenseState LoadState()
    {
        StateGate.Wait();
        try
        {
            PersistedLicenseState state = LoadStateCore();
            DateTime now = DateTime.UtcNow;
            if (now > state.LastSeenUtc)
            {
                state.LastSeenUtc = now;
                SaveStateCore(state);
            }

            return state;
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static PersistedLicenseState LoadStateCore()
    {
        DateTime registryInstallDate = GetOrCreateRegistryInstallDateUtc();
        string path = GetStatePath();

        PersistedLicenseState? state = null;
        try
        {
            if (File.Exists(path))
            {
                byte[] protectedBytes = File.ReadAllBytes(path);
                byte[] jsonBytes = DecryptState(protectedBytes);
                state = JsonSerializer.Deserialize<PersistedLicenseState>(jsonBytes, SerializerOptions);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Lisans durum dosyası okunamadı; güvenli varsayılan durum kullanılacak.", ex);
        }

        state ??= new PersistedLicenseState();
        if (state.InstallDateUtc == default)
            state.InstallDateUtc = registryInstallDate;
        else if (registryInstallDate < state.InstallDateUtc)
            state.InstallDateUtc = registryInstallDate;

        if (state.LastSeenUtc == default)
            state.LastSeenUtc = state.InstallDateUtc;

        state.ProductCode ??= ProductCode;
        state.MachineId ??= GetMachineId();

        return state;
    }

    private static void SaveStateCore(PersistedLicenseState state)
    {
        try
        {
            string path = GetStatePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            state.SchemaVersion = 1;
            state.ProductCode ??= ProductCode;
            state.MachineId ??= GetMachineId();
            if (state.InstallDateUtc == default)
                state.InstallDateUtc = GetOrCreateRegistryInstallDateUtc();
            if (DateTime.UtcNow > state.LastSeenUtc)
                state.LastSeenUtc = DateTime.UtcNow;

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
            byte[] protectedBytes = EncryptState(jsonBytes);
            string tempPath = path + ".tmp";
            File.WriteAllBytes(tempPath, protectedBytes);
            File.Move(tempPath, path, true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Lisans bilgileri güvenli depoya yazılamadı.", ex);
        }
    }

    private static DateTime GetEffectiveUtcNow(PersistedLicenseState state)
    {
        DateTime now = DateTime.UtcNow;
        if (state.LastSeenUtc != default && now < state.LastSeenUtc.AddHours(-6))
            return state.LastSeenUtc;

        return now;
    }

    private static byte[] EncryptState(byte[] plaintext)
    {
        byte[] key = GetStateEncryptionKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[plaintext.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(ProductCode));

        byte[] header = Encoding.ASCII.GetBytes("NSXL1");
        byte[] output = new byte[header.Length + nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(header, 0, output, 0, header.Length);
        Buffer.BlockCopy(nonce, 0, output, header.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, header.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, output, header.Length + nonce.Length + tag.Length, ciphertext.Length);
        return output;
    }

    private static byte[] DecryptState(byte[] protectedBytes)
    {
        byte[] header = Encoding.ASCII.GetBytes("NSXL1");
        const int nonceLength = 12;
        const int tagLength = 16;

        if (protectedBytes.Length <= header.Length + nonceLength + tagLength)
            throw new InvalidDataException("Lisans durum dosyası geçersiz.");

        if (!protectedBytes.AsSpan(0, header.Length).SequenceEqual(header))
            throw new InvalidDataException("Lisans durum dosyası sürümü desteklenmiyor.");

        ReadOnlySpan<byte> nonce = protectedBytes.AsSpan(header.Length, nonceLength);
        ReadOnlySpan<byte> tag = protectedBytes.AsSpan(header.Length + nonceLength, tagLength);
        ReadOnlySpan<byte> ciphertext = protectedBytes.AsSpan(header.Length + nonceLength + tagLength);
        byte[] plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(GetStateEncryptionKey(), tagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(ProductCode));
        return plaintext;
    }

    private static byte[] GetStateEncryptionKey() =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"NSX-LICENSE-STATE-V1|{ProductCode}|{GetMachineId()}|NSX-YAZILIM"));

    private static string GetStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NSX Yazılım",
        "NSX Veri Kurtarma Pro",
        "license-state.dat");

    private static DateTime GetOrCreateRegistryInstallDateUtc()
    {
        DateTime now = DateTime.UtcNow;
        const string path = @"Software\NSX Yazilim\NSX Veri Kurtarma Pro";
        const string name = "InstallDateUtc";

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            string? stored = key.GetValue(name)?.ToString();
            if (DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
                return parsed.ToUniversalTime();

            key.SetValue(name, now.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        }
        catch
        {
        }

        return now;
    }

    private sealed class PersistedLicenseState
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTime InstallDateUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? Email { get; set; }
        public string? LicenseKey { get; set; }
        public string? ProductCode { get; set; }
        public string? MachineId { get; set; }
        public string? LicenseType { get; set; }
        public string? LicenseMode { get; set; }
        public bool IsActive { get; set; }
        public DateTime? ActivationDateUtc { get; set; }
        public DateTime? ExpireDateUtc { get; set; }
        public DateTime? LastOnlineCheckUtc { get; set; }
        public string? LastOnlineResult { get; set; }
    }
}
