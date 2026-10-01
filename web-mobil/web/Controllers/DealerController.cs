using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Controllers
{
    [Route("Bayi")]
    public class DealerController : Controller
    {
        private const string DealerSessionKey = "NSX.Dealer.Id";
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly CampaignDiscountService _campaignDiscountService;

        public DealerController(
            ApplicationDbContext context,
            IConfiguration configuration,
            CampaignDiscountService campaignDiscountService)
        {
            _context = context;
            _configuration = configuration;
            _campaignDiscountService = campaignDiscountService;
        }

        [HttpGet("Giris")]
        public async Task<IActionResult> Login()
        {
            if (await GetCurrentDealerAsync() != null)
                return RedirectToAction(nameof(Index));

            return View(new DealerLoginViewModel());
        }

        [HttpPost("Giris")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(DealerLoginViewModel model)
        {
            model.Email = (model.Email ?? string.Empty).Trim().ToLowerInvariant();

            if (!ModelState.IsValid)
                return View(model);

            var dealer = await _context.Dealers.FirstOrDefaultAsync(x => x.Email == model.Email && x.IsActive);
            if (dealer == null || !PasswordHasher.Verify(model.Password ?? string.Empty, dealer.PasswordHash))
            {
                ModelState.AddModelError(string.Empty, "Bayi e-posta veya sifresi hatali.");
                return View(model);
            }

            HttpContext.Session.SetInt32(DealerSessionKey, dealer.Id);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Cikis")]
        [ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            HttpContext.Session.Remove(DealerSessionKey);
            return RedirectToAction(nameof(Login));
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var dealer = await GetCurrentDealerAsync();
            if (dealer == null)
                return RedirectToAction(nameof(Login));

            var allSales = await LoadDealerSalesAsync(dealer.Id);
            var approved = allSales.Where(IsApprovedSale).ToList();

            var model = new DealerDashboardViewModel
            {
                Dealer = dealer,
                Sales = allSales.Take(50).ToList(),
                TotalSales = allSales.Count,
                PendingSales = allSales.Count(x => !IsApprovedSale(x) && !IsRejectedSale(x)),
                ApprovedSales = approved.Count,
                ApprovedCommission = approved.Sum(x => x.CommissionAmount)
            };

            return View(model);
        }

        [HttpGet("Satislarim")]
        public async Task<IActionResult> Sales()
        {
            var dealer = await GetCurrentDealerAsync();
            if (dealer == null)
                return RedirectToAction(nameof(Login));

            ViewBag.Dealer = dealer;
            return View(await LoadDealerSalesAsync(dealer.Id));
        }

        [HttpGet("Satis/Yeni")]
        public async Task<IActionResult> NewSale()
        {
            var dealer = await GetCurrentDealerAsync();
            if (dealer == null)
                return RedirectToAction(nameof(Login));

            ViewBag.Dealer = dealer;
            return View(await PrepareSaleModelAsync(new DealerNewSaleViewModel()));
        }

        [HttpPost("Satis/Yeni")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NewSale(DealerNewSaleViewModel model)
        {
            var dealer = await GetCurrentDealerAsync();
            if (dealer == null)
                return RedirectToAction(nameof(Login));

            var customer = await _context.Users.FirstOrDefaultAsync(x =>
                x.Id == model.CustomerUserId &&
                x.Role == "User" &&
                x.IsActive &&
                !x.IsDeleted);

            var product = await _context.Products.FirstOrDefaultAsync(x =>
                x.Id == model.ProductId &&
                x.IsActive &&
                !x.IsDeleted);

            if (customer == null)
                ModelState.AddModelError(nameof(model.CustomerUserId), "Secilen musteri aktif bir site uyesi degil.");

            if (product == null)
                ModelState.AddModelError(nameof(model.ProductId), "Secilen urun bulunamadi.");
            else if (product.StockQuantity < model.Quantity)
                ModelState.AddModelError(nameof(model.Quantity), "Urun stogu bu satis icin yetersiz.");
            else if (product.YearlyPrice <= 0 && product.LifetimePrice <= 0)
                ModelState.AddModelError(nameof(model.ProductId), "Ucretsiz urun bayi satis kanalindan satilamaz.");

            model.LicenseType = NormalizeLicenseType(model.LicenseType);
            if (model.Quantity < 1 || model.Quantity > 10)
                ModelState.AddModelError(nameof(model.Quantity), "Adet 1 ile 10 arasinda olmalidir.");

            if (!ModelState.IsValid || customer == null || product == null)
            {
                ViewBag.Dealer = dealer;
                return View(await PrepareSaleModelAsync(model));
            }

            var listPrice = model.LicenseType == "Lifetime" ? product.LifetimePrice : product.YearlyPrice;
            if (listPrice <= 0)
            {
                ModelState.AddModelError(nameof(model.LicenseType), "Bu lisans tipi icin satis fiyati tanimli degil.");
                ViewBag.Dealer = dealer;
                return View(await PrepareSaleModelAsync(model));
            }

            var discount = await _campaignDiscountService.CalculateBestDiscountAsync(listPrice);
            var unitPrice = discount.GrandTotal;
            var saleAmount = decimal.Round(unitPrice * model.Quantity, 2, MidpointRounding.AwayFromZero);
            var commissionMode = string.Equals(dealer.CommissionMode, "Fixed", StringComparison.OrdinalIgnoreCase) ? "Fixed" : "Percent";
            var commissionAmount = commissionMode == "Fixed"
                ? dealer.CommissionFixedAmount
                : saleAmount * dealer.CommissionPercent / 100m;
            commissionAmount = Math.Min(saleAmount, Math.Max(0m, decimal.Round(commissionAmount, 2, MidpointRounding.AwayFromZero)));

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var order = new Order
                {
                    UserId = customer.Id,
                    OrderNumber = CreateDealerOrderNumber(),
                    TotalAmount = saleAmount,
                    PaymentStatus = "BankTransferPending",
                    OrderStatus = "WaitingBankTransfer",
                    CreatedAt = DateTime.Now
                };

                order.OrderItems.Add(new OrderItem
                {
                    ProductId = product.Id,
                    Quantity = model.Quantity,
                    UnitPrice = unitPrice,
                    LicenseType = model.LicenseType
                });

                _context.Orders.Add(order);
                await _context.SaveChangesAsync();

                var sale = new DealerSale
                {
                    DealerId = dealer.Id,
                    CustomerUserId = customer.Id,
                    ProductId = product.Id,
                    OrderId = order.Id,
                    LicenseType = model.LicenseType,
                    Quantity = model.Quantity,
                    SaleAmount = saleAmount,
                    CommissionModeSnapshot = commissionMode,
                    CommissionPercentSnapshot = dealer.CommissionPercent,
                    CommissionFixedAmountSnapshot = dealer.CommissionFixedAmount,
                    CommissionAmount = commissionAmount,
                    Status = "WaitingBankTransfer",
                    CreatedAt = DateTime.Now
                };

                _context.DealerSales.Add(sale);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                TempData["Success"] = "Satis kaydi olusturuldu. Simdi havale bildirimini tamamlayin.";
                return RedirectToAction(nameof(BankTransfer), new { id = sale.Id });
            }
            catch
            {
                await transaction.RollbackAsync();
                ModelState.AddModelError(string.Empty, "Bayi satis kaydi olusturulamadi. Lutfen tekrar deneyin.");
                ViewBag.Dealer = dealer;
                return View(await PrepareSaleModelAsync(model));
            }
        }

        [HttpGet("Satis/{id:int}")]
        public async Task<IActionResult> SaleDetail(int id)
        {
            var dealer = await GetCurrentDealerAsync();
            if (dealer == null)
                return RedirectToAction(nameof(Login));

            var sale = await LoadSaleAsync(id, dealer.Id);
            if (sale == null)
                return NotFound();

            ViewBag.Dealer = dealer;
            return View(sale);
        }

        [HttpGet("Satis/{id:int}/Havale")]
        public async Task<IActionResult> BankTransfer(int id)
        {
            var dealer = await GetCurrentDealerAsync();
            if (dealer == null)
                return RedirectToAction(nameof(Login));

            var sale = await LoadSaleAsync(id, dealer.Id);
            if (sale == null)
                return NotFound();

            if (sale.BankTransferNotificationId.HasValue)
                return RedirectToAction(nameof(SaleDetail), new { id = sale.Id });

            var model = new DealerBankTransferViewModel
            {
                SaleId = sale.Id,
                Sale = sale,
                SenderName = dealer.BusinessName,
                TransferDate = DateTime.Today
            };
            PrepareBankInfo(model);
            ViewBag.Dealer = dealer;
            return View(model);
        }

        [HttpPost("Satis/{id:int}/Havale")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BankTransfer(int id, DealerBankTransferViewModel model)
        {
            var dealer = await GetCurrentDealerAsync();
            if (dealer == null)
                return RedirectToAction(nameof(Login));

            var sale = await LoadSaleAsync(id, dealer.Id);
            if (sale == null)
                return NotFound();

            if (sale.BankTransferNotificationId.HasValue)
                return RedirectToAction(nameof(SaleDetail), new { id = sale.Id });

            model.SaleId = sale.Id;
            model.Sale = sale;
            PrepareBankInfo(model);

            if (string.IsNullOrWhiteSpace(model.SenderName))
                ModelState.AddModelError(nameof(model.SenderName), "Gonderen adini yazin.");

            if (!ModelState.IsValid || sale.CustomerUser == null || sale.Product == null)
            {
                ViewBag.Dealer = dealer;
                return View(model);
            }

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var notification = new BankTransferNotification
                {
                    ProductId = sale.ProductId,
                    UserId = sale.CustomerUserId,
                    OrderId = sale.OrderId,
                    FullName = sale.CustomerUser.FullName,
                    Email = sale.CustomerUser.Email,
                    Phone = sale.CustomerUser.Phone,
                    LicenseType = sale.LicenseType,
                    Quantity = sale.Quantity,
                    Amount = sale.SaleAmount,
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

                sale.BankTransferNotificationId = notification.Id;
                sale.BankTransferSubmittedAt = DateTime.Now;
                sale.Status = "PendingAdminApproval";

                if (sale.Order != null)
                {
                    sale.Order.PaymentStatus = "BankTransferPending";
                    sale.Order.OrderStatus = "WaitingBankTransfer";
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                TempData["Success"] = "Havale bildirimi admin onayina gonderildi.";
                return RedirectToAction(nameof(SaleDetail), new { id = sale.Id });
            }
            catch
            {
                await transaction.RollbackAsync();
                ModelState.AddModelError(string.Empty, "Havale bildirimi kaydedilemedi. Lutfen tekrar deneyin.");
                ViewBag.Dealer = dealer;
                return View(model);
            }
        }

        [HttpGet("Satis/{saleId:int}/Indir/{fileId:int}")]
        public async Task<IActionResult> DownloadFile(int saleId, int fileId)
        {
            var dealer = await GetCurrentDealerAsync();
            if (dealer == null)
                return RedirectToAction(nameof(Login));

            var sale = await _context.DealerSales
                .Include(x => x.BankTransferNotification)
                .Include(x => x.Order)
                    .ThenInclude(x => x!.Licenses)
                .FirstOrDefaultAsync(x => x.Id == saleId && x.DealerId == dealer.Id);

            if (sale == null)
                return NotFound();

            if (!IsApprovedSale(sale) || sale.Order == null || !sale.Order.Licenses.Any())
            {
                TempData["Error"] = "Program indirme, admin havale onayindan ve lisans olusumundan sonra acilir.";
                return RedirectToAction(nameof(SaleDetail), new { id = sale.Id });
            }

            var file = await _context.ProductFiles.FirstOrDefaultAsync(x =>
                x.Id == fileId && x.ProductId == sale.ProductId && x.FileType == "Program");

            if (file == null || string.IsNullOrWhiteSpace(file.FilePath))
            {
                TempData["Error"] = "Indirme dosyasi bulunamadi.";
                return RedirectToAction(nameof(SaleDetail), new { id = sale.Id });
            }

            var relativePath = file.FilePath
                .TrimStart('/', '\\')
                .Replace("/", Path.DirectorySeparatorChar.ToString())
                .Replace("\\", Path.DirectorySeparatorChar.ToString());

            var path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", relativePath);
            if (!System.IO.File.Exists(path))
            {
                TempData["Error"] = "Dosya sunucuda bulunamadi.";
                return RedirectToAction(nameof(SaleDetail), new { id = sale.Id });
            }

            var downloadName = string.IsNullOrWhiteSpace(file.OriginalFileName)
                ? Path.GetFileName(path)
                : file.OriginalFileName;

            return PhysicalFile(path, "application/octet-stream", downloadName);
        }

        private async Task<Dealer?> GetCurrentDealerAsync()
        {
            var dealerId = HttpContext.Session.GetInt32(DealerSessionKey);
            if (!dealerId.HasValue)
                return null;

            var dealer = await _context.Dealers.FirstOrDefaultAsync(x => x.Id == dealerId.Value && x.IsActive);
            if (dealer == null)
                HttpContext.Session.Remove(DealerSessionKey);

            return dealer;
        }

        private async Task<List<DealerSale>> LoadDealerSalesAsync(int dealerId, int? take = null)
        {
            var query = _context.DealerSales
                .Include(x => x.CustomerUser)
                .Include(x => x.Product)
                    .ThenInclude(x => x!.Files)
                .Include(x => x.Order)
                    .ThenInclude(x => x!.Licenses)
                .Include(x => x.BankTransferNotification)
                .Where(x => x.DealerId == dealerId)
                .OrderByDescending(x => x.Id)
                .AsQueryable();

            if (take.HasValue)
                query = query.Take(take.Value);

            return await query.ToListAsync();
        }

        private async Task<DealerSale?> LoadSaleAsync(int saleId, int dealerId)
        {
            var sale = await _context.DealerSales
                .Include(x => x.CustomerUser)
                .Include(x => x.Product)
                    .ThenInclude(x => x!.Files)
                .Include(x => x.Order)
                    .ThenInclude(x => x!.Licenses)
                .Include(x => x.BankTransferNotification)
                .FirstOrDefaultAsync(x => x.Id == saleId && x.DealerId == dealerId);

            if (sale != null && sale.BankTransferNotificationId.HasValue && sale.BankTransferNotification == null)
            {
                sale.BankTransferNotificationId = null;
                sale.BankTransferSubmittedAt = null;
                sale.Status = "WaitingBankTransfer";
                await _context.SaveChangesAsync();
            }

            return sale;
        }

        private async Task<DealerNewSaleViewModel> PrepareSaleModelAsync(DealerNewSaleViewModel model)
        {
            model.Customers = await _context.Users
                .Where(x => x.Role == "User" && x.IsActive && !x.IsDeleted)
                .OrderBy(x => x.FullName)
                .ThenBy(x => x.Email)
                .ToListAsync();

            model.Products = await _context.Products
                .Where(x => x.IsActive && !x.IsDeleted && x.StockQuantity > 0 && (x.YearlyPrice > 0 || x.LifetimePrice > 0))
                .OrderBy(x => x.Name)
                .ToListAsync();

            return model;
        }

        private void PrepareBankInfo(DealerBankTransferViewModel model)
        {
            model.BankName = CleanConfig(_configuration["BankTransfer:BankName"]);
            model.AccountHolder = CleanConfig(_configuration["BankTransfer:AccountHolder"]);
            model.Iban = CleanConfig(_configuration["BankTransfer:Iban"]);
            model.BankBranch = CleanConfig(_configuration["BankTransfer:Branch"]);
        }

        private static bool IsApprovedSale(DealerSale sale)
        {
            return string.Equals(sale.BankTransferNotification?.Status, "Approved", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sale.Order?.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRejectedSale(DealerSale sale)
        {
            return string.Equals(sale.BankTransferNotification?.Status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sale.Order?.PaymentStatus, "Rejected", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLicenseType(string? licenseType)
        {
            return string.Equals(licenseType, "Lifetime", StringComparison.OrdinalIgnoreCase) ? "Lifetime" : "Yearly";
        }

        private static string CreateDealerOrderNumber()
        {
            return $"NSX-BAYI-{DateTime.Now:yyyyMMddHHmmss}-{Random.Shared.Next(100, 999)}";
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
