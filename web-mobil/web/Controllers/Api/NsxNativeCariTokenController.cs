using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NSYazilim.Web.CaritakipCloud.Models;
using NSYazilim.Web.CaritakipCloud.Services;

namespace NSYazilim.Web.Controllers.Api;

[ApiController]
[Route("api/nsx-native/cari/token")]
[EnableRateLimiting("CaritakipCloud")]
public sealed class NsxNativeCariTokenController : ControllerBase
{
    [HttpPost("rotate")]
    public async Task<IActionResult> Rotate(
        [FromBody] CariNativeRefreshRequest? request,
        [FromServices] CaritakipNativeMobileStore nativeStore,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

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

                return StatusCode(StatusCodes.Status401Unauthorized, new { error, message });
            }

            return Ok(new
            {
                accessToken = refreshed.Result.AccessToken,
                refreshToken = refreshed.Result.RefreshToken,
                accessTokenExpiresAt = refreshed.Result.AccessTokenExpiresAtUtc,
                refreshTokenExpiresAt = refreshed.Result.RefreshTokenExpiresAtUtc
            });
        }
        catch (CariEntitlementUnavailableException ex)
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "SERVICE_UNAVAILABLE", message = ex.Message });
        }
    }
}
