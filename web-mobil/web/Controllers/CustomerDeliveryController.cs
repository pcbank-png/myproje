using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.ViewModels;
using System.Security.Claims;
using System.Text;

namespace NSYazilim.Web.Controllers
{
    [Route("Account")]
    public class CustomerDeliveryController : Controller
    {
        private readonly ApplicationDbContext _context;

        public CustomerDeliveryController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("OrderDetail/{id:int}")]
        public async Task<IActionResult> OrderDetail(int id)
        {
            var userId = GetCurrentUserId();

            if (userId == null)
                return RedirectToAction("Login", "Account", new { returnUrl = $"/Account/OrderDetail/{id}" });

            var order = await _context.Orders
                .Include(x => x.OrderItems)
                .ThenInclude(x => x.Product)
                .Include(x => x.Licenses)
                .ThenInclude(x => x.Product)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId.Value);

            if (order == null)
                return NotFound();

            var model = new OrderDeliveryViewModel
            {
                Order = order,
                Licenses = order.Licenses.OrderByDescending(x => x.Id).ToList()
            };

            return View("~/Views/Account/OrderDetail.cshtml", model);
        }

        [HttpGet("LicenseText/{id:int}")]
        public async Task<IActionResult> LicenseText(int id)
        {
            var userId = GetCurrentUserId();

            if (userId == null)
                return RedirectToAction("Login", "Account", new { returnUrl = $"/Account/LicenseText/{id}" });

            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.Order)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId.Value);

            if (license == null)
                return NotFound();

            var text = new StringBuilder();
            text.AppendLine("NSX Yazılım Lisans Bilgisi");
            text.AppendLine("--------------------------");
            text.AppendLine($"Ürün: {license.Product?.Name}");
            text.AppendLine($"Lisans Anahtarı: {license.LicenseKey}");
            text.AppendLine($"Lisans Tipi: {(license.LicenseType == "Lifetime" ? "Sınırsız" : "Yıllık")}");
            text.AppendLine($"Başlangıç: {license.StartDate:dd.MM.yyyy}");
            text.AppendLine($"Bitiş: {(license.EndDate == null ? "Sınırsız" : license.EndDate.Value.ToString("dd.MM.yyyy"))}");
            text.AppendLine($"Sipariş: {license.Order?.OrderNumber}");

            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            return File(bytes, "text/plain", $"NSX-Lisans-{license.LicenseKey}.txt");
        }

        private int? GetCurrentUserId()
        {
            var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(idValue, out var id) ? id : null;
        }
    }
}
