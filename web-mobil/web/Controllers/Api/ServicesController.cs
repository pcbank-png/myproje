using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.Controllers.Api
{
    [ApiController]
    [Route("api")]
    public class ServicesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ServicesController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            var databaseOk = false;

            try
            {
                databaseOk = await _context.Database.CanConnectAsync();
            }
            catch
            {
                databaseOk = false;
            }

            return Ok(new
            {
                success = databaseOk,
                status = databaseOk ? "Healthy" : "DatabaseUnavailable",
                service = "NSX Yazılım API",
                checkedAtUtc = DateTime.UtcNow,
                endpoints = new[]
                {
                    "/api/license/v2/activate",
                    "/api/license/v2/check",
                    "/License/Activate",
                    "/License/Status",
                    "/api/update/check",
                    "/api/ads/active",
                    "/api/services",
                    "/api/status"
                }
            });
        }

        [HttpGet("services")]
        public IActionResult Services()
        {
            return Ok(new
            {
                success = true,
                service = "NSX Yazılım API Servisleri",
                version = "1.0",
                endpoints = new[]
                {
                    new
                    {
                        name = "Lisans V2 Aktivasyon",
                        method = "POST",
                        path = "/api/license/v2/activate",
                        auth = "x-api-key header",
                        parameters = new[] { "email", "licenseKey", "productCode", "machineId", "deviceName", "appVersion", "osVersion" }
                    },
                    new
                    {
                        name = "Lisans V2 Kontrol",
                        method = "POST",
                        path = "/api/license/v2/check",
                        auth = "x-api-key header",
                        parameters = new[] { "email", "licenseKey", "productCode", "machineId", "appVersion" }
                    },
                    new
                    {
                        name = "Lisans Aktivasyon (Eski)",
                        method = "GET",
                        path = "/License/Activate",
                        auth = "x-api-key header veya apiKey query",
                        parameters = new[] { "key", "machineId", "email", "apiKey", "appVersion", "version" }
                    },
                    new
                    {
                        name = "Lisans Durum",
                        method = "GET",
                        path = "/License/Status",
                        auth = "x-api-key header veya apiKey query",
                        parameters = new[] { "key", "machineId", "apiKey", "appVersion", "version" }
                    },
                    new
                    {
                        name = "Program Güncelleme Kontrol",
                        method = "GET",
                        path = "/api/update/check",
                        auth = "Yok",
                        parameters = new[] { "productCode", "version", "licenseKey (opsiyonel)", "machineId (opsiyonel)" }
                    },
                    new
                    {
                        name = "Aktif Reklam",
                        method = "GET",
                        path = "/api/ads/active",
                        auth = "Yok",
                        parameters = new[] { "productCode", "slot" }
                    },
                    new
                    {
                        name = "API Sağlık Kontrol",
                        method = "GET",
                        path = "/api/status",
                        auth = "Yok",
                        parameters = Array.Empty<string>()
                    }
                }
            });
        }
    }
}
