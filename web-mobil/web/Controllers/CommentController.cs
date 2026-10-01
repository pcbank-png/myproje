using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using System.Security.Claims;

namespace NSYazilim.Web.Controllers
{
    public class CommentController : Controller
    {
        private readonly ApplicationDbContext _context;

        public CommentController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Add(int productId, string comment, int rating)
        {
            var productUrl = await GetProductPublicUrlAsync(productId);
            rating = Math.Clamp(rating, 1, 5);
            comment = comment?.Trim() ?? string.Empty;

            if (comment.Length > 2000)
            {
                TempData["Error"] = "Yorum en fazla 2000 karakter olabilir.";
                return LocalRedirect($"{productUrl}#yorumlar");
            }

            var productExists = await _context.Products.AnyAsync(x =>
                x.Id == productId &&
                !x.IsDeleted &&
                x.IsActive);

            if (!productExists)
            {
                TempData["Error"] = "Yorum yapılacak ürün bulunamadı.";
                return LocalRedirect(productUrl);
            }

            var user = await GetCurrentCustomerAsync();
            if (user == null)
            {
                return RedirectToAction("Login", "Account", new { returnUrl = $"{productUrl}#yorumlar" });
            }

            _context.ProductComments.Add(new ProductComment
            {
                ProductId = productId,
                UserId = user.Id,
                FullName = user.FullName,
                Comment = comment,
                Rating = rating,
                IsApproved = false,
                IsDeleted = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            TempData["Success"] = string.IsNullOrWhiteSpace(comment)
                ? "Puanınız alındı. Admin onayından sonra ürün puanına eklenecek."
                : "Yorumunuz ve puanınız alındı. Admin onayından sonra yayınlanacak.";
            return LocalRedirect($"{productUrl}#yorumlar");
        }

        private async Task<User?> GetCurrentCustomerAsync()
        {
            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdValue, out var userId))
                return null;

            return await _context.Users.FirstOrDefaultAsync(x =>
                x.Id == userId &&
                !x.IsDeleted &&
                x.IsActive &&
                x.Role == "User");
        }

        private async Task<string> GetProductPublicUrlAsync(int productId)
        {
            var product = await _context.Products
                .AsNoTracking()
                .Where(x => x.Id == productId && !x.IsDeleted && x.IsActive)
                .Select(x => new { x.Id, x.Slug })
                .FirstOrDefaultAsync();

            if (product == null)
                return $"/store/detail/{productId}";

            return !string.IsNullOrWhiteSpace(product.Slug)
                ? $"/urun/{product.Slug.Trim().ToLowerInvariant()}"
                : $"/store/detail/{product.Id}";
        }
    }
}
