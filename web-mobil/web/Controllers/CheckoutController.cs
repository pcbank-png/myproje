using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Controllers
{
    [Route("Odeme")]
    public sealed class CheckoutController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly CampaignDiscountService _campaignDiscountService;
        private readonly ShopierService _shopierService;

        public CheckoutController(
            ApplicationDbContext context,
            CampaignDiscountService campaignDiscountService,
            ShopierService shopierService)
        {
            _context = context;
            _campaignDiscountService = campaignDiscountService;
            _shopierService = shopierService;
        }

        [HttpGet("")]
        public async Task<IActionResult> Payment(int productId, string licenseType = "Yearly")
        {
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(currentUserId, out var userId))
            {
                var returnUrl = Request.Path + Request.QueryString.ToString();
                return RedirectToAction("Login", "Account", new { returnUrl });
            }

            var currentUser = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.Id == userId && !x.IsDeleted && x.IsActive && x.Role == "User");
            if (currentUser == null)
            {
                var returnUrl = Request.Path + Request.QueryString.ToString();
                return RedirectToAction("Login", "Account", new { returnUrl });
            }

            var product = await _context.Products
                .AsNoTracking()
                .Include(x => x.Images)
                .FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted && x.IsActive);

            if (product == null)
                return NotFound();

            if (product.YearlyPrice <= 0 && product.LifetimePrice <= 0)
                return RedirectToAction("DownloadFree", "Store", new { productId = product.Id });

            if (product.StockQuantity <= 0)
            {
                TempData["Warning"] = "Bu ürün şu anda stokta yok.";
                return Redirect(GetProductPublicUrl(product));
            }

            licenseType = string.Equals(licenseType, "Lifetime", StringComparison.OrdinalIgnoreCase)
                ? "Lifetime"
                : "Yearly";

            var basePrice = licenseType == "Lifetime" ? product.LifetimePrice : product.YearlyPrice;
            var discount = await _campaignDiscountService.CalculateBestDiscountAsync(basePrice);
            var amount = discount.GrandTotal;
            var shopierUrl = await _shopierService.GetProductCheckoutUrlAsync(product, HttpContext.RequestAborted);

            var model = new CheckoutPaymentViewModel
            {
                Product = product,
                LicenseType = licenseType,
                LicenseLabel = licenseType == "Lifetime" ? "Sınırsız Lisans" : "Yıllık Lisans",
                Amount = amount,
                ShopierUrl = shopierUrl ?? string.Empty,
                ProductUrl = GetProductPublicUrl(product),
                BankTransferUrl = $"/Havale-Bildirimi?productId={product.Id}&licenseType={licenseType}",
                CustomerEmail = currentUser.Email
            };

            return View("Payment", model);
        }

        private static string GetProductPublicUrl(NSYazilim.Web.Models.Product product)
        {
            return !string.IsNullOrWhiteSpace(product.Slug)
                ? $"/urun/{product.Slug.Trim().ToLowerInvariant()}"
                : $"/store/detail/{product.Id}";
        }
    }
}
