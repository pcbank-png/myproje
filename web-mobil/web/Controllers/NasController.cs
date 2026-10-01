using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers;

[Route("nas")]
[AutoValidateAntiforgeryToken]
public sealed class NasController : Controller
{
    private const string CredentialsSessionKey = "NSX.NASS.WebDav.Credentials.v1";
    private const string WebDavBaseUrl = "https://nsxnas.keenetic.pro/webdav/NAS/";
    private static readonly TimeSpan LoginAttemptWindow = TimeSpan.FromMinutes(10);
    private const int MaxLoginAttempts = 6;
    private static readonly Uri WebDavBaseUri = new(WebDavBaseUrl, UriKind.Absolute);
    private static readonly string WebDavBasePath = Uri.UnescapeDataString(WebDavBaseUri.AbsolutePath);
    private static readonly ConcurrentDictionary<string, LoginAttemptState> LoginAttempts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, UploadProgressState> UploadProgressStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions BatchJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IDataProtector _credentialProtector;
    private readonly ClientIpService _clientIpService;
    private readonly ILogger<NasController> _logger;

    public NasController(
        IDataProtectionProvider dataProtectionProvider,
        ClientIpService clientIpService,
        ILogger<NasController> logger)
    {
        _credentialProtector = dataProtectionProvider.CreateProtector("NSYazilim.NASS.WebDav.SessionCredentials.v1");
        _clientIpService = clientIpService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? path = null, CancellationToken cancellationToken = default)
    {
        SetSecurityHeaders();

        string currentPath;
        try
        {
            currentPath = NormalizeRelativePath(path);
        }
        catch
        {
            return RedirectToAction(nameof(Index));
        }

        var credentials = GetCredentials();
        var model = new NasPageViewModel
        {
            IsAuthenticated = credentials != null,
            CurrentPath = currentPath,
            UserName = credentials?.UserName ?? string.Empty
        };

        if (credentials == null)
            return View(model);

        try
        {
            model.Items = await ListDirectoryAsync(credentials, currentPath, cancellationToken);
        }
        catch (NasWebDavException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            ClearCredentials();
            model.IsAuthenticated = false;
            model.UserName = string.Empty;
            model.CurrentPath = string.Empty;
            model.ErrorMessage = "NAS oturumunun süresi doldu veya erişim bilgileri değişti. Lütfen yeniden giriş yapın.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAS WebDAV klasörü listelenemedi. Path: {Path}", currentPath);
            model.ErrorMessage = "NAS'a şu anda ulaşılamıyor. Modem, USB bellek ve Kişisel Bulut bağlantısını kontrol edin.";
        }

        return View(model);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(string userName, string password, CancellationToken cancellationToken = default)
    {
        SetSecurityHeaders();
        userName = (userName ?? string.Empty).Trim();
        password ??= string.Empty;

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return View("Index", new NasPageViewModel
            {
                IsAuthenticated = false,
                UserName = userName,
                ErrorMessage = "Kullanıcı adı ve parola gerekli."
            });
        }

        var loginKey = _clientIpService.GetClientIp(HttpContext);
        if (IsLoginBlocked(loginKey, out var retryAfter))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return View("Index", new NasPageViewModel
            {
                IsAuthenticated = false,
                UserName = userName,
                ErrorMessage = $"Çok fazla hatalı giriş denemesi yapıldı. Yaklaşık {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes))} dakika sonra tekrar deneyin."
            });
        }

        var credentials = new NasCredentials(userName, password);

        try
        {
            _ = await ListDirectoryAsync(credentials, string.Empty, cancellationToken);
            LoginAttempts.TryRemove(loginKey, out _);
            SaveCredentials(credentials);
            return RedirectToAction(nameof(Index));
        }
        catch (NasWebDavException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            RegisterLoginFailure(loginKey);
            return View("Index", new NasPageViewModel
            {
                IsAuthenticated = false,
                UserName = userName,
                ErrorMessage = "Kullanıcı adı, parola veya WebDAV erişim yetkisi hatalı."
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAS WebDAV giriş denemesinde bağlantı hatası oluştu.");
            return View("Index", new NasPageViewModel
            {
                IsAuthenticated = false,
                UserName = userName,
                ErrorMessage = "NAS'a bağlanılamadı. Keenetic Kişisel Bulut servisinin açık olduğundan emin olun."
            });
        }
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        ClearCredentials();
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("download")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Download(string path, CancellationToken cancellationToken = default)
    {
        var credentials = GetCredentials();
        if (credentials == null)
            return RedirectToAction(nameof(Index));

        string normalizedPath;
        try
        {
            normalizedPath = NormalizeRelativePath(path);
            if (string.IsNullOrEmpty(normalizedPath))
                return BadRequest();
        }
        catch
        {
            return BadRequest();
        }

        using var client = CreateClient(credentials);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildWebDavUri(normalizedPath, asDirectory: false));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            ClearCredentials();
            return RedirectToAction(nameof(Index));
        }

        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode);

        var fileName = GetLastSegment(normalizedPath);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var contentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = fileName
        };

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = contentType;
        Response.Headers["Content-Disposition"] = contentDisposition.ToString();

        if (response.Content.Headers.ContentLength is long contentLength)
            Response.ContentLength = contentLength;

        await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await remoteStream.CopyToAsync(Response.Body, cancellationToken);
        return new EmptyResult();
    }

    [HttpPost("create-folder")]
    public async Task<IActionResult> CreateFolder(string currentPath, string name, CancellationToken cancellationToken = default)
    {
        var credentials = GetCredentials();
        if (credentials == null)
            return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);

        try
        {
            currentPath = NormalizeRelativePath(currentPath);
            name = ValidateEntryName(name);
            var targetPath = CombineRelativePath(currentPath, name);

            using var client = CreateClient(credentials);
            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), BuildWebDavUri(targetPath, asDirectory: true));
            using var response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                ClearCredentials();
                return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode == HttpStatusCode.MethodNotAllowed
                    ? "Bu isimde bir klasör zaten var."
                    : "Klasör oluşturulamadı.";
                return JsonError(message, (int)response.StatusCode);
            }

            return JsonOk("Klasör oluşturuldu.");
        }
        catch (ArgumentException ex)
        {
            return JsonError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAS klasör oluşturma işlemi başarısız.");
            return JsonError("Klasör oluşturulamadı. Bağlantıyı kontrol edin.", StatusCodes.Status502BadGateway);
        }
    }

    [HttpPost("upload")]
    [RequestSizeLimit(1_073_741_824)]
    [RequestFormLimits(MultipartBodyLengthLimit = 1_073_741_824)]
    public async Task<IActionResult> Upload(
        string currentPath,
        string uploadId,
        List<IFormFile> files,
        CancellationToken cancellationToken = default)
    {
        var credentials = GetCredentials();
        if (credentials == null)
            return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);

        if (files == null || files.Count == 0)
            return JsonError("Yüklenecek dosya seçilmedi.");

        if (!Guid.TryParse(uploadId, out var parsedUploadId))
            return JsonError("Yükleme kimliği geçersiz.");

        var progressKey = parsedUploadId.ToString("N");
        CleanupUploadProgressStates();

        UploadProgressState? progress = null;
        try
        {
            currentPath = NormalizeRelativePath(currentPath);
            var acceptedFiles = files.Where(x => x.Length >= 0).ToArray();
            var totalBytes = acceptedFiles.Sum(x => x.Length);
            progress = new UploadProgressState(HttpContext.Session.Id, totalBytes);
            UploadProgressStates[progressKey] = progress;

            using var client = CreateClient(credentials);

            foreach (var file in acceptedFiles)
            {
                var safeName = ValidateEntryName(GetUploadFileName(file.FileName));
                var targetPath = CombineRelativePath(currentPath, safeName);
                progress.SetCurrentFile(safeName);

                await using var source = file.OpenReadStream();
                using var trackedSource = new ProgressReadStream(source, progress.AddTransferredBytes);
                using var content = new StreamContent(trackedSource);
                if (MediaTypeHeaderValue.TryParse(file.ContentType, out var mediaType))
                    content.Headers.ContentType = mediaType;
                else
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentLength = file.Length;

                using var request = new HttpRequestMessage(HttpMethod.Put, BuildWebDavUri(targetPath, asDirectory: false))
                {
                    Content = content
                };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    progress.MarkFailed("Oturum sona erdi.");
                    ClearCredentials();
                    return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);
                }

                if (!response.IsSuccessStatusCode)
                {
                    progress.MarkFailed($"{safeName} yüklenemedi.");
                    return JsonError($"{safeName} yüklenemedi.", (int)response.StatusCode);
                }
            }

            progress.MarkCompleted();
            return Json(new
            {
                ok = true,
                message = files.Count == 1 ? "Dosya yüklendi." : $"{files.Count} dosya yüklendi.",
                uploadId = progressKey,
                totalBytes
            });
        }
        catch (ArgumentException ex)
        {
            progress?.MarkFailed(ex.Message);
            return JsonError(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress?.MarkFailed("Yükleme iptal edildi.");
            return JsonError("Yükleme iptal edildi.", 499);
        }
        catch (Exception ex)
        {
            progress?.MarkFailed("Dosya yüklenemedi.");
            _logger.LogWarning(ex, "NAS dosya yükleme işlemi başarısız.");
            return JsonError("Dosya yüklenemedi. Bağlantıyı kontrol edin.", StatusCodes.Status502BadGateway);
        }
    }

    [HttpGet("upload-progress")]
    [IgnoreAntiforgeryToken]
    public IActionResult UploadProgress(string id)
    {
        if (GetCredentials() == null)
            return Unauthorized();

        if (!Guid.TryParse(id, out var parsedUploadId))
            return BadRequest();

        CleanupUploadProgressStates();
        var progressKey = parsedUploadId.ToString("N");
        if (!UploadProgressStates.TryGetValue(progressKey, out var state) ||
            !string.Equals(state.SessionId, HttpContext.Session.Id, StringComparison.Ordinal))
        {
            return Json(new { ok = true, found = false });
        }

        var snapshot = state.GetSnapshot();
        return Json(new
        {
            ok = true,
            found = true,
            totalBytes = snapshot.TotalBytes,
            transferredBytes = snapshot.TransferredBytes,
            currentFile = snapshot.CurrentFile,
            stage = snapshot.Stage,
            error = snapshot.Error
        });
    }

    [HttpPost("rename")]
    public async Task<IActionResult> Rename(string path, string newName, bool isDirectory, CancellationToken cancellationToken = default)
    {
        var credentials = GetCredentials();
        if (credentials == null)
            return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);

        try
        {
            var sourcePath = NormalizeRelativePath(path);
            if (string.IsNullOrEmpty(sourcePath))
                return JsonError("Ana klasör yeniden adlandırılamaz.");

            newName = ValidateEntryName(newName);
            var parentPath = GetParentPath(sourcePath);
            var destinationPath = CombineRelativePath(parentPath, newName);

            using var client = CreateClient(credentials);
            using var request = new HttpRequestMessage(new HttpMethod("MOVE"), BuildWebDavUri(sourcePath, isDirectory));
            request.Headers.TryAddWithoutValidation("Destination", BuildWebDavUri(destinationPath, isDirectory).AbsoluteUri);
            request.Headers.TryAddWithoutValidation("Overwrite", "F");
            using var response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                ClearCredentials();
                return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict
                    ? "Bu isimde başka bir dosya veya klasör zaten var."
                    : "Yeniden adlandırma işlemi tamamlanamadı.";
                return JsonError(message, (int)response.StatusCode);
            }

            return JsonOk("Ad değiştirildi.");
        }
        catch (ArgumentException ex)
        {
            return JsonError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAS yeniden adlandırma işlemi başarısız.");
            return JsonError("Ad değiştirilemedi. Bağlantıyı kontrol edin.", StatusCodes.Status502BadGateway);
        }
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        var credentials = GetCredentials();
        if (credentials == null)
            return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);

        try
        {
            path = NormalizeRelativePath(path);
            if (string.IsNullOrEmpty(path))
                return JsonError("Ana klasör silinemez.");

            using var client = CreateClient(credentials);
            using var request = new HttpRequestMessage(HttpMethod.Delete, BuildWebDavUri(path, isDirectory));
            using var response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                ClearCredentials();
                return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);
            }

            if (!response.IsSuccessStatusCode)
                return JsonError("Silme işlemi tamamlanamadı.", (int)response.StatusCode);

            return JsonOk("Silindi.");
        }
        catch (ArgumentException ex)
        {
            return JsonError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAS silme işlemi başarısız.");
            return JsonError("Silme işlemi tamamlanamadı. Bağlantıyı kontrol edin.", StatusCodes.Status502BadGateway);
        }
    }

    [HttpPost("delete-batch")]
    public async Task<IActionResult> DeleteBatch(string itemsJson, CancellationToken cancellationToken = default)
    {
        var credentials = GetCredentials();
        if (credentials == null)
            return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);

        try
        {
            var items = ParseBatchItems(itemsJson);
            using var client = CreateClient(credentials);

            foreach (var item in items)
            {
                var path = NormalizeRelativePath(item.Path);
                if (string.IsNullOrEmpty(path))
                    return JsonError("Ana klasör silinemez.");

                using var request = new HttpRequestMessage(HttpMethod.Delete, BuildWebDavUri(path, item.IsDirectory));
                using var response = await client.SendAsync(request, cancellationToken);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    ClearCredentials();
                    return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);
                }

                if (!response.IsSuccessStatusCode)
                    return JsonError($"{item.Name} silinemedi.", (int)response.StatusCode);
            }

            return JsonOk(items.Count == 1 ? "Seçilen öğe silindi." : $"{items.Count} seçili öğe silindi.");
        }
        catch (ArgumentException ex)
        {
            return JsonError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAS toplu silme işlemi başarısız.");
            return JsonError("Seçilen öğeler silinemedi. Bağlantıyı kontrol edin.", StatusCodes.Status502BadGateway);
        }
    }

    [HttpPost("copy-batch")]
    public async Task<IActionResult> CopyBatch(
        string destinationPath,
        string itemsJson,
        CancellationToken cancellationToken = default)
    {
        var credentials = GetCredentials();
        if (credentials == null)
            return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);

        try
        {
            destinationPath = NormalizeRelativePath(destinationPath);
            var items = ParseBatchItems(itemsJson);
            using var client = CreateClient(credentials);

            foreach (var item in items)
            {
                var sourcePath = NormalizeRelativePath(item.Path);
                if (string.IsNullOrEmpty(sourcePath))
                    return JsonError("Ana klasör kopyalanamaz.");

                if (item.IsDirectory &&
                    (string.Equals(destinationPath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                     destinationPath.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    return JsonError($"{item.Name} kendi içine kopyalanamaz.");
                }

                var targetPath = CombineRelativePath(destinationPath, GetLastSegment(sourcePath));
                if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    return JsonError($"{item.Name} zaten bu klasörde. Başka bir klasöre yapıştırın.");

                using var request = new HttpRequestMessage(new HttpMethod("COPY"), BuildWebDavUri(sourcePath, item.IsDirectory));
                request.Headers.TryAddWithoutValidation("Destination", BuildWebDavUri(targetPath, item.IsDirectory).AbsoluteUri);
                request.Headers.TryAddWithoutValidation("Overwrite", "F");
                if (item.IsDirectory)
                    request.Headers.TryAddWithoutValidation("Depth", "infinity");

                using var response = await client.SendAsync(request, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    ClearCredentials();
                    return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var message = response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict
                        ? $"{item.Name} hedef klasörde zaten var."
                        : $"{item.Name} kopyalanamadı.";
                    return JsonError(message, (int)response.StatusCode);
                }
            }

            return JsonOk(items.Count == 1 ? "Seçilen öğe kopyalandı." : $"{items.Count} seçili öğe kopyalandı.");
        }
        catch (ArgumentException ex)
        {
            return JsonError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAS toplu kopyalama işlemi başarısız.");
            return JsonError("Seçilen öğeler kopyalanamadı. Bağlantıyı kontrol edin.", StatusCodes.Status502BadGateway);
        }
    }


    [HttpPost("folder-sizes")]
    public async Task<IActionResult> FolderSizes(string pathsJson, CancellationToken cancellationToken = default)
    {
        var credentials = GetCredentials();
        if (credentials == null)
            return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);

        try
        {
            var paths = ParseFolderPaths(pathsJson);
            using var client = CreateClient(credentials);
            var sizes = new List<object>(paths.Count);

            foreach (var path in paths)
            {
                try
                {
                    var size = await GetFolderSizeAsync(client, path, cancellationToken);
                    sizes.Add(new { path, ok = true, size, sizeText = FormatBytes(size) });
                }
                catch (NasWebDavException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    ClearCredentials();
                    return JsonError("Oturum sona erdi. Lütfen yeniden giriş yapın.", StatusCodes.Status401Unauthorized);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "NAS klasör boyutu hesaplanamadı. Path: {Path}", path);
                    sizes.Add(new { path, ok = false, size = (long?)null, sizeText = "—" });
                }
            }

            return Json(new { ok = true, sizes });
        }
        catch (ArgumentException ex)
        {
            return JsonError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NAS klasör boyutları alınamadı.");
            return JsonError("Klasör boyutları hesaplanamadı. Bağlantıyı kontrol edin.", StatusCodes.Status502BadGateway);
        }
    }

    private async Task<long> GetFolderSizeAsync(HttpClient client, string relativePath, CancellationToken cancellationToken)
    {
        relativePath = NormalizeRelativePath(relativePath);
        if (string.IsNullOrEmpty(relativePath))
            throw new ArgumentException("Ana klasör için boyut hesaplanamaz.");

        // Folder collections do not expose an aggregate Content-Length in WebDAV.
        // Walk the tree with Depth: 1 so nested folders are included reliably on Keenetic too.
        return await GetFolderSizeRecursivelyAsync(client, relativePath, cancellationToken);
    }

    private async Task<long> GetFolderSizeRecursivelyAsync(
        HttpClient client,
        string rootPath,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(rootPath);
        visited.Add(rootPath);
        long total = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderPath = pending.Dequeue();

            using var request = CreatePropFindRequest(BuildWebDavUri(folderPath, asDirectory: true), "1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new NasWebDavException(response.StatusCode, "WebDAV erişimi reddedildi.");

            if (!response.IsSuccessStatusCode)
                throw new NasWebDavException(response.StatusCode, "WebDAV klasör boyutu alınamadı.");

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(xml))
                continue;

            if (!TryParseWebDavDocument(xml, out var document))
                throw new NasWebDavException(response.StatusCode, "WebDAV klasör boyutu yanıtı okunamadı.");

            XNamespace dav = "DAV:";
            foreach (var responseNode in document.Descendants(dav + "response"))
            {
                var href = responseNode.Element(dav + "href")?.Value;
                var itemPath = GetRelativePathFromHref(href);
                if (itemPath == null || string.Equals(itemPath, folderPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.Equals(GetParentPath(itemPath), folderPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var prop = responseNode.Descendants(dav + "prop").FirstOrDefault();
                if (prop == null)
                    continue;

                var isDirectory = prop.Element(dav + "resourcetype")?.Element(dav + "collection") != null;
                if (isDirectory)
                {
                    if (visited.Add(itemPath))
                        pending.Enqueue(itemPath);
                    continue;
                }

                if (long.TryParse(
                        prop.Element(dav + "getcontentlength")?.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsedSize))
                {
                    total += Math.Max(0, parsedSize);
                }
                else
                {
                    var fallbackLength = await TryGetRemoteFileLengthAsync(client, itemPath, cancellationToken);
                    if (fallbackLength.HasValue)
                        total += Math.Max(0, fallbackLength.Value);
                }
            }
        }

        return total;
    }

    private async Task<long?> TryGetRemoteFileLengthAsync(
        HttpClient client,
        string relativePath,
        CancellationToken cancellationToken)
    {
        using (var headRequest = new HttpRequestMessage(HttpMethod.Head, BuildWebDavUri(relativePath, asDirectory: false)))
        using (var headResponse = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            if (headResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new NasWebDavException(headResponse.StatusCode, "WebDAV erişimi reddedildi.");

            if (headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentLength is long headLength)
                return headLength;
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, BuildWebDavUri(relativePath, asDirectory: false));
        using var getResponse = await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (getResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new NasWebDavException(getResponse.StatusCode, "WebDAV erişimi reddedildi.");

        return getResponse.IsSuccessStatusCode ? getResponse.Content.Headers.ContentLength : null;
    }

    private static bool TryParseWebDavDocument(string xml, out XDocument document)
    {
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
            return true;
        }
        catch
        {
            document = new XDocument();
            return false;
        }
    }

    private async Task<IReadOnlyList<NasEntryViewModel>> ListDirectoryAsync(
        NasCredentials credentials,
        string relativePath,
        CancellationToken cancellationToken)
    {
        relativePath = NormalizeRelativePath(relativePath);
        using var client = CreateClient(credentials);
        using var request = CreatePropFindRequest(BuildWebDavUri(relativePath, asDirectory: true), "1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new NasWebDavException(response.StatusCode, "WebDAV klasör listesi alınamadı.");

        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
            return Array.Empty<NasEntryViewModel>();

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (Exception ex)
        {
            throw new NasWebDavException(response.StatusCode, "WebDAV yanıtı okunamadı.", ex);
        }

        XNamespace dav = "DAV:";
        var items = new List<NasEntryViewModel>();

        foreach (var responseNode in document.Descendants(dav + "response"))
        {
            var href = responseNode.Element(dav + "href")?.Value;
            var itemPath = GetRelativePathFromHref(href);
            if (itemPath == null || string.Equals(itemPath, relativePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(GetParentPath(itemPath), relativePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var prop = responseNode.Descendants(dav + "prop").FirstOrDefault();
            if (prop == null)
                continue;

            var isDirectory = prop.Element(dav + "resourcetype")?.Element(dav + "collection") != null;
            var displayName = prop.Element(dav + "displayname")?.Value?.Trim();
            var name = string.IsNullOrWhiteSpace(displayName) ? GetLastSegment(itemPath) : displayName;

            long? size = null;
            if (!isDirectory && long.TryParse(
                    prop.Element(dav + "getcontentlength")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedSize))
            {
                size = parsedSize;
            }

            DateTimeOffset? lastModified = null;
            if (DateTimeOffset.TryParse(
                    prop.Element(dav + "getlastmodified")?.Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var parsedDate))
            {
                lastModified = parsedDate;
            }

            items.Add(new NasEntryViewModel
            {
                Name = name,
                Path = itemPath,
                IsDirectory = isDirectory,
                Size = size,
                SizeText = isDirectory ? "—" : FormatBytes(size),
                LastModified = lastModified,
                ContentType = prop.Element(dav + "getcontenttype")?.Value ?? string.Empty
            });
        }

        var comparer = StringComparer.Create(new CultureInfo("tr-TR"), ignoreCase: true);
        return items
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, comparer)
            .ToArray();
    }

    private static HttpRequestMessage CreatePropFindRequest(Uri uri, string depth)
    {
        const string body = """
            <?xml version="1.0" encoding="utf-8" ?>
            <d:propfind xmlns:d="DAV:">
              <d:prop>
                <d:displayname />
                <d:resourcetype />
                <d:getcontentlength />
                <d:getlastmodified />
                <d:getcontenttype />
              </d:prop>
            </d:propfind>
            """;

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        request.Headers.TryAddWithoutValidation("Depth", depth);
        return request;
    }

    private static HttpClient CreateClient(NasCredentials credentials)
    {
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(credentials.UserName, credentials.Password),
            PreAuthenticate = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NSX-NAS-WebDAV/1.0");
        client.DefaultRequestHeaders.ExpectContinue = false;
        return client;
    }

    private NasCredentials? GetCredentials()
    {
        var protectedPayload = HttpContext.Session.GetString(CredentialsSessionKey);
        if (string.IsNullOrWhiteSpace(protectedPayload))
            return null;

        try
        {
            var json = _credentialProtector.Unprotect(protectedPayload);
            return JsonSerializer.Deserialize<NasCredentials>(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NAS oturum bilgisi çözülemedi; oturum temizleniyor.");
            ClearCredentials();
            return null;
        }
    }

    private void SaveCredentials(NasCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        HttpContext.Session.SetString(CredentialsSessionKey, _credentialProtector.Protect(json));
    }

    private void ClearCredentials()
    {
        HttpContext.Session.Remove(CredentialsSessionKey);
    }

    private void SetSecurityHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
            "script-src 'self' 'unsafe-inline'; connect-src 'self'; form-action 'self'; " +
            "frame-ancestors 'none'; base-uri 'none'";
    }

    private static Uri BuildWebDavUri(string relativePath, bool asDirectory)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var encodedPath = string.Join('/', normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

        var absolute = WebDavBaseUrl + encodedPath;
        if (asDirectory && !absolute.EndsWith("/", StringComparison.Ordinal))
            absolute += "/";

        return new Uri(absolute, UriKind.Absolute);
    }

    private static string? GetRelativePathFromHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        string path;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
            path = absoluteUri.AbsolutePath;
        else
            path = href.Split('?', '#')[0];

        path = Uri.UnescapeDataString(path).Replace('\\', '/');
        if (!path.StartsWith(WebDavBasePath, StringComparison.OrdinalIgnoreCase))
            return null;

        return NormalizeRelativePath(path[WebDavBasePath.Length..]);
    }

    private static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var decoded = Uri.UnescapeDataString(path.Trim()).Replace('\\', '/').Trim('/');
        if (decoded.Length == 0)
            return string.Empty;

        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Any(char.IsControl))
                throw new ArgumentException("Geçersiz klasör yolu.");
        }

        return string.Join('/', segments);
    }

    private static string ValidateEntryName(string? name)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Dosya veya klasör adı boş olamaz.");

        if (name is "." or "..")
            throw new ArgumentException("Bu ad kullanılamaz.");

        if (name.IndexOfAny(new[] { '/', '\\', '<', '>', ':', '"', '|', '?', '*' }) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Dosya veya klasör adında geçersiz karakter var.");

        if (name.EndsWith(' ') || name.EndsWith('.'))
            throw new ArgumentException("Dosya veya klasör adı boşluk ya da nokta ile bitemez.");

        if (name.Length > 180)
            throw new ArgumentException("Dosya veya klasör adı çok uzun.");

        return name;
    }

    private static string CombineRelativePath(string parentPath, string name)
    {
        parentPath = NormalizeRelativePath(parentPath);
        return string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
    }

    private static string GetParentPath(string path)
    {
        path = NormalizeRelativePath(path);
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string GetLastSegment(string path)
    {
        path = NormalizeRelativePath(path);
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string GetUploadFileName(string fileName)
    {
        fileName = (fileName ?? string.Empty).Replace('\\', '/');
        var index = fileName.LastIndexOf('/');
        return index < 0 ? fileName : fileName[(index + 1)..];
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes == null)
            return "—";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }



    private static IReadOnlyList<string> ParseFolderPaths(string? pathsJson)
    {
        if (string.IsNullOrWhiteSpace(pathsJson))
            return Array.Empty<string>();

        List<string>? paths;
        try
        {
            paths = JsonSerializer.Deserialize<List<string>>(pathsJson, BatchJsonOptions);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Klasör bilgisi okunamadı.");
        }

        if (paths == null || paths.Count == 0)
            return Array.Empty<string>();

        if (paths.Count > 100)
            throw new ArgumentException("Tek seferde en fazla 100 klasörün boyutu hesaplanabilir.");

        var result = new List<string>(paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in paths)
        {
            var path = NormalizeRelativePath(rawPath);
            if (!string.IsNullOrEmpty(path) && seen.Add(path))
                result.Add(path);
        }

        return result;
    }

    private static IReadOnlyList<NasBatchItem> ParseBatchItems(string? itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson))
            throw new ArgumentException("Seçili öğe bulunamadı.");

        List<NasBatchItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<NasBatchItem>>(itemsJson, BatchJsonOptions);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Seçili öğe bilgisi okunamadı.");
        }

        if (items == null || items.Count == 0)
            throw new ArgumentException("Seçili öğe bulunamadı.");

        if (items.Count > 200)
            throw new ArgumentException("Tek seferde en fazla 200 öğe işlenebilir.");

        var normalized = new List<NasBatchItem>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var path = NormalizeRelativePath(item.Path);
            if (string.IsNullOrEmpty(path) || !seen.Add(path))
                continue;

            var name = string.IsNullOrWhiteSpace(item.Name) ? GetLastSegment(path) : item.Name.Trim();
            normalized.Add(new NasBatchItem(path, item.IsDirectory, name));
        }

        if (normalized.Count == 0)
            throw new ArgumentException("Geçerli bir seçili öğe bulunamadı.");

        return normalized;
    }

    private static void CleanupUploadProgressStates()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var pair in UploadProgressStates)
        {
            if (pair.Value.LastUpdatedUtc < cutoff)
                UploadProgressStates.TryRemove(pair.Key, out _);
        }
    }

    private static bool IsLoginBlocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!LoginAttempts.TryGetValue(key, out var state))
            return false;

        var elapsed = DateTimeOffset.UtcNow - state.WindowStartedUtc;
        if (elapsed >= LoginAttemptWindow)
        {
            LoginAttempts.TryRemove(key, out _);
            return false;
        }

        if (state.Failures < MaxLoginAttempts)
            return false;

        retryAfter = LoginAttemptWindow - elapsed;
        return true;
    }

    private static void RegisterLoginFailure(string key)
    {
        var now = DateTimeOffset.UtcNow;
        LoginAttempts.AddOrUpdate(
            key,
            _ => new LoginAttemptState(1, now),
            (_, current) => now - current.WindowStartedUtc >= LoginAttemptWindow
                ? new LoginAttemptState(1, now)
                : current with { Failures = current.Failures + 1 });
    }

    private IActionResult JsonOk(string message)
    {
        return Json(new { ok = true, message });
    }

    private IActionResult JsonError(string message, int statusCode = StatusCodes.Status400BadRequest)
    {
        Response.StatusCode = statusCode;
        return Json(new { ok = false, message });
    }

    private sealed record NasCredentials(string UserName, string Password);
    private sealed record LoginAttemptState(int Failures, DateTimeOffset WindowStartedUtc);
    private sealed record NasBatchItem(string Path, bool IsDirectory, string Name);
    private sealed record UploadProgressSnapshot(long TotalBytes, long TransferredBytes, string CurrentFile, string Stage, string? Error);

    private sealed class UploadProgressState
    {
        private long _transferredBytes;
        private long _lastUpdatedUtcTicks = DateTime.UtcNow.Ticks;
        private string _currentFile = string.Empty;
        private string? _error;
        private int _stage;

        public UploadProgressState(string sessionId, long totalBytes)
        {
            SessionId = sessionId;
            TotalBytes = Math.Max(0, totalBytes);
        }

        public string SessionId { get; }
        public long TotalBytes { get; }
        public DateTime LastUpdatedUtc => new(Interlocked.Read(ref _lastUpdatedUtcTicks), DateTimeKind.Utc);

        public void SetCurrentFile(string fileName)
        {
            Volatile.Write(ref _currentFile, fileName ?? string.Empty);
            Touch();
        }

        public void AddTransferredBytes(long bytes)
        {
            if (bytes <= 0)
                return;

            Interlocked.Add(ref _transferredBytes, bytes);
            Touch();
        }

        public void MarkCompleted()
        {
            Interlocked.Exchange(ref _transferredBytes, TotalBytes);
            Volatile.Write(ref _stage, 1);
            Touch();
        }

        public void MarkFailed(string message)
        {
            Volatile.Write(ref _error, message);
            Volatile.Write(ref _stage, 2);
            Touch();
        }

        public UploadProgressSnapshot GetSnapshot()
        {
            var stage = Volatile.Read(ref _stage) switch
            {
                1 => "completed",
                2 => "failed",
                _ => "forwarding"
            };

            return new UploadProgressSnapshot(
                TotalBytes,
                Math.Min(TotalBytes, Math.Max(0, Interlocked.Read(ref _transferredBytes))),
                Volatile.Read(ref _currentFile) ?? string.Empty,
                stage,
                Volatile.Read(ref _error));
        }

        private void Touch() => Interlocked.Exchange(ref _lastUpdatedUtcTicks, DateTime.UtcNow.Ticks);
    }

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onRead;

        public ProgressReadStream(Stream inner, Action<long> onRead)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _onRead = onRead ?? throw new ArgumentNullException(nameof(onRead));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (read > 0) _onRead(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (read > 0) _onRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            if (read > 0) _onRead(read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            // The IFormFile stream is owned by the upload action and disposed there.
            base.Dispose(disposing);
        }
    }

    private sealed class NasWebDavException : Exception
    {
        public NasWebDavException(HttpStatusCode statusCode, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}

public sealed class NasPageViewModel
{
    public bool IsAuthenticated { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = string.Empty;
    public IReadOnlyList<NasEntryViewModel> Items { get; set; } = Array.Empty<NasEntryViewModel>();
    public string? ErrorMessage { get; set; }
}

public sealed class NasEntryViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long? Size { get; set; }
    public string SizeText { get; set; } = "—";
    public DateTimeOffset? LastModified { get; set; }
    public string ContentType { get; set; } = string.Empty;
}
