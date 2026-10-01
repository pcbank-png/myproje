$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$programPath = Join-Path $root 'Program.cs'
$publishBat = Join-Path $root 'PUBLISH_NSX_WEB_PLESK_NATIVE_MOBILE_SAGLAM.bat'

if (-not (Test-Path -LiteralPath $programPath)) {
    throw "Program.cs bulunamadi. Bu paketi NSYazilim.Web.csproj ile ayni ana klasore koyun."
}

$text = [System.IO.File]::ReadAllText($programPath)
$anchor = 'app.MapHub<LiveChatHub>("/liveChatHub");'
$beginMarker = '// NSX_NATIVE_ROTATE_HARD_ROUTE_BEGIN'
$endMarker = '// NSX_NATIVE_ROTATE_HARD_ROUTE_END'

if (-not $text.Contains($anchor)) {
    throw "Program.cs icinde beklenen LiveChatHub anchor satiri bulunamadi. Dosya otomatik degistirilmedi."
}

# Idempotent: remove a previous copy of this exact hard-route block first.
$pattern = [regex]::Escape($beginMarker) + '.*?' + [regex]::Escape($endMarker) + '\r?\n?'
$text = [regex]::Replace($text, $pattern, '', [System.Text.RegularExpressions.RegexOptions]::Singleline)

$block = @'
// NSX_NATIVE_ROTATE_HARD_ROUTE_BEGIN
// Native mobile refresh token rotation is intentionally mapped at Program level
// on a unique path to bypass the legacy GET-only route collision on Plesk/IIS.
app.MapPost("/api/nsx-native-token/rotate", async Task<IResult> (
    NSYazilim.Web.CaritakipCloud.Models.CariNativeRefreshRequest? request,
    HttpContext http,
    CaritakipNativeMobileStore nativeStore,
    CancellationToken cancellationToken) =>
{
    http.Response.Headers.CacheControl = "no-store";

    try
    {
        var refreshed = await nativeStore.RefreshAsync(
            request?.RefreshToken ?? string.Empty,
            cancellationToken);

        if (refreshed.Result is null)
        {
            var error = refreshed.Error ?? "REFRESH_REVOKED";
            var message = error switch
            {
                "REFRESH_EXPIRED" => "Mobil oturum yenileme anahtarının süresi dolmuş.",
                "DEVICE_UNAUTHORIZED" => "Bu mobil cihazın erişim yetkisi kaldırılmış.",
                _ => "Mobil oturum yenileme anahtarı geçersiz veya iptal edilmiş."
            };

            return Results.Json(
                new { error, message },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new
        {
            accessToken = refreshed.Result.AccessToken,
            refreshToken = refreshed.Result.RefreshToken,
            accessTokenExpiresAt = refreshed.Result.AccessTokenExpiresAtUtc,
            refreshTokenExpiresAt = refreshed.Result.RefreshTokenExpiresAtUtc
        });
    }
    catch (CariEntitlementUnavailableException ex)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.Json(
            new { error = "SERVICE_UNAVAILABLE", message = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.RequireRateLimiting("CaritakipCloud");
// NSX_NATIVE_ROTATE_HARD_ROUTE_END
'@

$newText = $text.Replace($anchor, $anchor + "`r`n`r`n" + $block)
if ($newText -eq $text) {
    throw "Program.cs degisikligi uygulanamadi."
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$programPath.rotate-backup-$stamp"
Copy-Item -LiteralPath $programPath -Destination $backup -Force

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($programPath, $newText, $utf8Bom)

Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host ' NSX NATIVE ROTATE HARD ROUTE UYGULANDI' -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
Write-Host 'Yeni endpoint:' -ForegroundColor Cyan
Write-Host 'POST /api/nsx-native-token/rotate' -ForegroundColor White
Write-Host ''
Write-Host "Yedek: $backup" -ForegroundColor DarkGray
Write-Host ''

if (Test-Path -LiteralPath $publishBat) {
    Write-Host 'Publish BAT bulundu.' -ForegroundColor Cyan
    Write-Host 'Simdi PUBLISH_NSX_WEB_PLESK_NATIVE_MOBILE_SAGLAM.bat dosyasini calistirin.' -ForegroundColor White
} else {
    Write-Host 'UYARI: PUBLISH_NSX_WEB_PLESK_NATIVE_MOBILE_SAGLAM.bat bu klasorde bulunamadi.' -ForegroundColor Yellow
}
