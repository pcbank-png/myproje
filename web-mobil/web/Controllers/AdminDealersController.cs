using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("Admin/Bayiler")]
    public class AdminDealersController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AdminDealersController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var dealers = await _context.Dealers
                .Include(x => x.Sales)
                    .ThenInclude(x => x.BankTransferNotification)
                .OrderByDescending(x => x.IsActive)
                .ThenBy(x => x.Province)
                .ThenBy(x => x.BusinessName)
                .ToListAsync();

            ViewBag.ActiveDealerCount = dealers.Count(x => x.IsActive);
            ViewBag.PendingSaleCount = dealers.SelectMany(x => x.Sales)
                .Count(x => x.BankTransferNotificationId == null || x.BankTransferNotification == null || x.BankTransferNotification.Status == "Pending");
            ViewBag.ApprovedCommission = dealers.SelectMany(x => x.Sales)
                .Where(x => x.BankTransferNotification != null && x.BankTransferNotification.Status == "Approved")
                .Sum(x => x.CommissionAmount);

            return View(dealers);
        }

        [HttpGet("Yeni")]
        public IActionResult Create()
        {
            return View("Form", PrepareForm(new AdminDealerFormViewModel
            {
                IsActive = true,
                CommissionMode = "Percent"
            }));
        }

        [HttpPost("Yeni")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AdminDealerFormViewModel model)
        {
            Normalize(model);

            if (string.IsNullOrWhiteSpace(model.Password) || model.Password.Length < 6)
                ModelState.AddModelError(nameof(model.Password), "Bayi sifresi en az 6 karakter olmalidir.");

            if (await _context.Dealers.AnyAsync(x => x.Email == model.Email))
                ModelState.AddModelError(nameof(model.Email), "Bu e-posta ile kayitli bir bayi zaten var.");

            ValidateCommission(model);

            if (!ModelState.IsValid)
                return View("Form", PrepareForm(model));

            var dealer = new Dealer
            {
                DealerCode = await GenerateDealerCodeAsync(),
                BusinessName = model.BusinessName,
                ContactName = model.ContactName,
                Email = model.Email,
                Phone = CleanOptional(model.Phone),
                Province = model.Province,
                District = CleanOptional(model.District),
                Address = CleanOptional(model.Address),
                PasswordHash = PasswordHasher.Hash(model.Password!),
                CommissionMode = model.CommissionMode,
                CommissionPercent = model.CommissionPercent,
                CommissionFixedAmount = model.CommissionFixedAmount,
                IsActive = model.IsActive,
                CreatedAt = DateTime.Now
            };

            _context.Dealers.Add(dealer);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Bayi hesabi olusturuldu.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet("Duzenle/{id:int}")]
        public async Task<IActionResult> Edit(int id)
        {
            var dealer = await _context.Dealers.FirstOrDefaultAsync(x => x.Id == id);
            if (dealer == null)
                return NotFound();

            var model = new AdminDealerFormViewModel
            {
                Id = dealer.Id,
                BusinessName = dealer.BusinessName,
                ContactName = dealer.ContactName,
                Email = dealer.Email,
                Phone = dealer.Phone,
                Province = dealer.Province,
                District = dealer.District,
                Address = dealer.Address,
                CommissionMode = dealer.CommissionMode,
                CommissionPercent = dealer.CommissionPercent,
                CommissionFixedAmount = dealer.CommissionFixedAmount,
                IsActive = dealer.IsActive
            };

            return View("Form", PrepareForm(model));
        }

        [HttpPost("Duzenle/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, AdminDealerFormViewModel model)
        {
            var dealer = await _context.Dealers.FirstOrDefaultAsync(x => x.Id == id);
            if (dealer == null)
                return NotFound();

            model.Id = id;
            Normalize(model);

            if (await _context.Dealers.AnyAsync(x => x.Id != id && x.Email == model.Email))
                ModelState.AddModelError(nameof(model.Email), "Bu e-posta baska bir bayide kullaniliyor.");

            ValidateCommission(model);

            if (!ModelState.IsValid)
                return View("Form", PrepareForm(model));

            dealer.BusinessName = model.BusinessName;
            dealer.ContactName = model.ContactName;
            dealer.Email = model.Email;
            dealer.Phone = CleanOptional(model.Phone);
            dealer.Province = model.Province;
            dealer.District = CleanOptional(model.District);
            dealer.Address = CleanOptional(model.Address);
            dealer.CommissionMode = model.CommissionMode;
            dealer.CommissionPercent = model.CommissionPercent;
            dealer.CommissionFixedAmount = model.CommissionFixedAmount;
            dealer.IsActive = model.IsActive;
            dealer.UpdatedAt = DateTime.Now;

            if (!string.IsNullOrWhiteSpace(model.Password))
            {
                if (model.Password.Length < 6)
                {
                    ModelState.AddModelError(nameof(model.Password), "Bayi sifresi en az 6 karakter olmalidir.");
                    return View("Form", PrepareForm(model));
                }

                dealer.PasswordHash = PasswordHasher.Hash(model.Password);
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = "Bayi bilgileri guncellendi.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Durum/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var dealer = await _context.Dealers.FirstOrDefaultAsync(x => x.Id == id);
            if (dealer == null)
                return NotFound();

            dealer.IsActive = !dealer.IsActive;
            dealer.UpdatedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            TempData["Success"] = dealer.IsActive ? "Bayi aktif edildi." : "Bayi pasif edildi.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet("Satislar")]
        public async Task<IActionResult> Sales()
        {
            var sales = await _context.DealerSales
                .Include(x => x.Dealer)
                .Include(x => x.CustomerUser)
                .Include(x => x.Product)
                .Include(x => x.Order)
                    .ThenInclude(x => x!.Licenses)
                .Include(x => x.BankTransferNotification)
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            return View(sales);
        }

        [HttpPost("SatisSil/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSale(int id)
        {
            var sale = await _context.DealerSales.FirstOrDefaultAsync(x => x.Id == id);
            if (sale == null)
            {
                TempData["Error"] = "Bayi satis kaydi bulunamadi.";
                return RedirectToAction(nameof(Sales));
            }

            _context.DealerSales.Remove(sale);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Bayi satis kaydi silindi. Siparis, havale ve lisans kayitlari korundu.";
            return RedirectToAction(nameof(Sales));
        }

        private AdminDealerFormViewModel PrepareForm(AdminDealerFormViewModel model)
        {
            model.Provinces = TurkeyProvinceHelper.Provinces;
            return model;
        }

        private void ValidateCommission(AdminDealerFormViewModel model)
        {
            if (model.CommissionMode == "Percent" && (model.CommissionPercent < 0 || model.CommissionPercent > 100))
                ModelState.AddModelError(nameof(model.CommissionPercent), "Kar orani 0 ile 100 arasinda olmalidir.");

            if (model.CommissionMode == "Fixed" && model.CommissionFixedAmount < 0)
                ModelState.AddModelError(nameof(model.CommissionFixedAmount), "Sabit bayi kari negatif olamaz.");
        }

        private static void Normalize(AdminDealerFormViewModel model)
        {
            model.BusinessName = (model.BusinessName ?? string.Empty).Trim();
            model.ContactName = (model.ContactName ?? string.Empty).Trim();
            model.Email = (model.Email ?? string.Empty).Trim().ToLowerInvariant();
            model.Province = (model.Province ?? string.Empty).Trim();
            model.CommissionMode = string.Equals(model.CommissionMode, "Fixed", StringComparison.OrdinalIgnoreCase) ? "Fixed" : "Percent";
        }

        private async Task<string> GenerateDealerCodeAsync()
        {
            for (var i = 0; i < 50; i++)
            {
                var code = $"NSX-BAYI-{Random.Shared.Next(100000, 999999)}";
                if (!await _context.Dealers.AnyAsync(x => x.DealerCode == code))
                    return code;
            }

            return $"NSX-BAYI-{DateTime.Now:yyyyMMddHHmmss}";
        }

        private static string? CleanOptional(string? value)
        {
            var cleaned = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }
    }
}
