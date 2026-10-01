using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Controllers
{
    [Route("Havale-Bildirimi")]
    public class BankTransferController : Controller
    {
        private const string BankTransferDescriptionInstruction = "Lütfen Havale/EFT yaparken açıklama Kısmını boş bırakınız.";
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly CampaignDiscountService _campaignDiscountService;

        public BankTransferController(
            ApplicationDbContext context,
            IConfiguration configuration,
            CampaignDiscountService campaignDiscountService)
        {
            _context = context;
            _configuration = configuration;
            _campaignDiscountService = campaignDiscountService;
        }

        [HttpGet("")]
        public async Task<IActionResult> Create(int productId, string licenseType = "Yearly")
        {
            var user = await GetCurrentCustomerAsync();
            if (user == null)
            {
                var returnUrl = Request.Path + Request.QueryString.ToString();
                return RedirectToAction("Login", "Account", new { returnUrl });
            }

            var product = await _context.Products
                .Include(x => x.Images)
                .FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted && x.IsActive);

            if (product == null)
                return NotFound();

            if (IsFreeProduct(product))
                return RedirectToAction("DownloadFree", "Store", new { productId = product.Id });

            if (product.StockQuantity <= 0)
            {
                TempData["Warning"] = "Bu ürün şu anda stokta yok. Satın alma bildirimi kapalıdır.";
                return Redirect(GetProductPublicUrl(product));
            }

            licenseType = NormalizeLicenseType(licenseType);

            var model = new BankTransferNotificationViewModel
            {
                ProductId = product.Id,
                Product = product,
                LicenseType = licenseType,
                FullName = user.FullName,
                Email = user.Email,
                Phone = user.Phone,
                SenderName = user.FullName,
                TransferDate = DateTime.Today
            };
            await PrepareBankTransferModel(model, product);

            return View(model);
        }

        [HttpPost("")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(BankTransferNotificationViewModel model)
        {
            var user = await GetCurrentCustomerAsync();
            if (user == null)
            {
                var returnUrl = Request.Path + Request.QueryString.ToString();
                return RedirectToAction("Login", "Account", new { returnUrl });
            }

            var product = await _context.Products
                .Include(x => x.Images)
                .FirstOrDefaultAsync(x => x.Id == model.ProductId && !x.IsDeleted && x.IsActive);

            if (product == null)
                return NotFound();

            if (IsFreeProduct(product))
                return RedirectToAction("DownloadFree", "Store", new { productId = product.Id });

            model.Product = product;
            model.LicenseType = NormalizeLicenseType(model.LicenseType);
            model.FullName = user.FullName;
            model.Email = user.Email;
            model.Phone = user.Phone;
            await PrepareBankTransferModel(model, product);

            if (product.StockQuantity <= 0)
                ModelState.AddModelError(string.Empty, "Bu ürün şu anda stokta yok. Satın alma bildirimi kapalıdır.");

            if (string.IsNullOrWhiteSpace(model.SenderName))
                ModelState.AddModelError(nameof(model.SenderName), "Gönderen adını yazın.");

            if (!ModelState.IsValid)
                return View(model);

            await using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var order = new Order
                {
                    UserId = user.Id,
                    OrderNumber = CreateOrderNumber(),
                    TotalAmount = model.Amount,
                    PaymentStatus = "BankTransferPending",
                    OrderStatus = "WaitingBankTransfer",
                    CreatedAt = DateTime.Now
                };

                order.OrderItems.Add(new OrderItem
                {
                    ProductId = product.Id,
                    Quantity = 1,
                    UnitPrice = model.Amount,
                    LicenseType = model.LicenseType
                });

                _context.Orders.Add(order);
                await _context.SaveChangesAsync();

                var notification = new BankTransferNotification
                {
                    ProductId = product.Id,
                    UserId = user.Id,
                    OrderId = order.Id,
                    FullName = user.FullName,
                    Email = user.Email,
                    Phone = user.Phone,
                    LicenseType = model.LicenseType,
                    Quantity = 1,
                    Amount = model.Amount,
                    SenderName = model.SenderName.Trim(),
                    SenderBank = CleanOptional(model.SenderBank),
                    ReceiptNumber = CleanOptional(model.ReceiptNumber),
                    TransferDate = model.TransferDate == default ? DateTime.Today : model.TransferDate.Date,
                    Note = CleanOptional(model.Note),
                    Status = "Pending",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = Request.Headers["User-Agent"].ToString(),
                    CreatedAt = DateTime.Now
                };

                _context.BankTransferNotifications.Add(notification);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return RedirectToAction(nameof(Success), new { id = notification.Id });
            }
            catch
            {
                await transaction.RollbackAsync();
                TempData["Error"] = "Havale bildirimi kaydedilirken bir sorun oluştu. Lütfen tekrar deneyin.";
                return View(model);
            }
        }

        [HttpGet("Basarili/{id:int}")]
        public async Task<IActionResult> Success(int id)
        {
            var user = await GetCurrentCustomerAsync();
            if (user == null)
                return RedirectToAction("Login", "Account", new { returnUrl = $"/Havale-Bildirimi/Basarili/{id}" });

            var notification = await _context.BankTransferNotifications
                .Include(x => x.Product)
                .Include(x => x.Order)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);

            if (notification == null)
                return NotFound();

            return View(notification);
        }

        private async Task<User?> GetCurrentCustomerAsync()
        {
            var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!int.TryParse(idValue, out var userId))
                return null;

            return await _context.Users.FirstOrDefaultAsync(x =>
                x.Id == userId &&
                !x.IsDeleted &&
                x.IsActive &&
                x.Role == "User");
        }

        private static decimal GetLicensePrice(Product product, string licenseType)
        {
            return licenseType == "Lifetime" ? product.LifetimePrice : product.YearlyPrice;
        }

        private static bool IsFreeProduct(Product product)
        {
            return product.YearlyPrice <= 0 && product.LifetimePrice <= 0;
        }

        private async Task PrepareBankTransferModel(BankTransferNotificationViewModel model, Product product)
        {
            model.Product = product;
            model.LicenseType = NormalizeLicenseType(model.LicenseType);
            model.YearlyPrice = product.YearlyPrice;
            model.LifetimePrice = product.LifetimePrice;

            var yearlyDiscount = await _campaignDiscountService.CalculateBestDiscountAsync(product.YearlyPrice);
            var lifetimeDiscount = await _campaignDiscountService.CalculateBestDiscountAsync(product.LifetimePrice);

            model.YearlyPayableAmount = yearlyDiscount.GrandTotal;
            model.LifetimePayableAmount = lifetimeDiscount.GrandTotal;
            model.YearlyCampaignDiscountAmount = yearlyDiscount.DiscountAmount;
            model.LifetimeCampaignDiscountAmount = lifetimeDiscount.DiscountAmount;
            model.YearlyCampaignName = yearlyDiscount.CampaignName;
            model.LifetimeCampaignName = lifetimeDiscount.CampaignName;
            model.YearlyCampaignDiscountText = yearlyDiscount.DiscountText;
            model.LifetimeCampaignDiscountText = lifetimeDiscount.DiscountText;

            var selectedDiscount = model.LicenseType == "Lifetime" ? lifetimeDiscount : yearlyDiscount;
            model.OriginalAmount = GetLicensePrice(product, model.LicenseType);
            model.CampaignDiscountAmount = selectedDiscount.DiscountAmount;
            model.CampaignName = selectedDiscount.CampaignName;
            model.CampaignDiscountText = selectedDiscount.DiscountText;
            model.Amount = selectedDiscount.GrandTotal;
            model.BankName = CleanConfig(_configuration["BankTransfer:BankName"]);
            model.AccountHolder = CleanConfig(_configuration["BankTransfer:AccountHolder"]);
            model.Iban = CleanConfig(_configuration["BankTransfer:Iban"]);
            model.BankBranch = CleanConfig(_configuration["BankTransfer:Branch"]);
            model.YearlyTransferDescription = BankTransferDescriptionInstruction;
            model.LifetimeTransferDescription = BankTransferDescriptionInstruction;
            model.TransferDescription = model.LicenseType == "Lifetime"
                ? model.LifetimeTransferDescription
                : model.YearlyTransferDescription;
        }

        private static string NormalizeLicenseType(string? licenseType)
        {
            return string.Equals(licenseType, "Lifetime", StringComparison.OrdinalIgnoreCase)
                ? "Lifetime"
                : "Yearly";
        }

        private static string GetProductPublicUrl(Product product)
        {
            return !string.IsNullOrWhiteSpace(product.Slug)
                ? $"/urun/{product.Slug.Trim().ToLowerInvariant()}"
                : $"/store/detail/{product.Id}";
        }

        private static string CreateOrderNumber()
        {
            return $"NSX-HVL-{DateTime.Now:yyyyMMddHHmmss}-{Random.Shared.Next(100, 999)}";
        }

        private static string? CleanOptional(string? value)
        {
            var cleaned = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }

        private static string CleanConfig(string? value)
        {
            var cleaned = (value ?? string.Empty).Trim();
            if (cleaned.Contains("BURAYA", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return cleaned;
        }
    }
}
