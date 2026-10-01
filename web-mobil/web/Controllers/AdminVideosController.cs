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
    [Route("Admin/Videos")]
    public class AdminVideosController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly LocalizationTranslationQueueService _translationQueue;

        public AdminVideosController(ApplicationDbContext context, LocalizationTranslationQueueService translationQueue)
        {
            _context = context;
            _translationQueue = translationQueue;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var videos = await _context.ProductVideos
                .AsNoTracking()
                .Include(x => x.Product)
                .OrderByDescending(x => x.IsFeatured)
                .ThenBy(x => x.SortOrder)
                .ThenByDescending(x => x.Id)
                .ToListAsync();

            return View(videos);
        }

        [HttpGet("Create")]
        public async Task<IActionResult> Create()
        {
            return View(await PrepareFormAsync(new ProductVideoFormViewModel
            {
                IsActive = true,
                VideoType = "Tanıtım"
            }));
        }

        [HttpPost("Create")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ProductVideoFormViewModel model)
        {
            var product = await GetProductAsync(model.ProductId);
            ValidateVideoForm(model, product, out var videoId);

            if (!ModelState.IsValid || product == null)
                return View(await PrepareFormAsync(model));

            var slugBase = BuildSlugBase(product, model);
            var video = new ProductVideo
            {
                ProductId = product.Id,
                YouTubeVideoId = videoId,
                YouTubeUrl = YouTubeVideoHelper.NormalizeWatchUrl(videoId),
                Title = CleanOptional(model.Title),
                VideoType = CleanOptional(model.VideoType) ?? "Tanıtım",
                Slug = await CreateUniqueSlugAsync(slugBase),
                SortOrder = model.SortOrder,
                IsFeatured = model.IsFeatured,
                IsActive = model.IsActive,
                CreatedAt = DateTime.Now
            };

            _context.ProductVideos.Add(video);
            await _context.SaveChangesAsync();
            var queued = await _translationQueue.QueueVideoAsync(video, HttpContext.RequestAborted);

            TempData["Success"] = $"Video programa bağlandı ve kaydedildi. {queued} dil çevirisi kuyruğa alındı.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet("Edit/{id:int}")]
        public async Task<IActionResult> Edit(int id)
        {
            var video = await _context.ProductVideos
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (video == null)
                return NotFound();

            var model = new ProductVideoFormViewModel
            {
                Id = video.Id,
                ProductId = video.ProductId,
                YouTubeUrl = video.YouTubeUrl,
                Title = video.Title,
                VideoType = video.VideoType,
                SortOrder = video.SortOrder,
                IsFeatured = video.IsFeatured,
                IsActive = video.IsActive
            };

            return View(await PrepareFormAsync(model));
        }

        [HttpPost("Edit/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, ProductVideoFormViewModel model)
        {
            if (id != model.Id)
                return BadRequest();

            var video = await _context.ProductVideos.FirstOrDefaultAsync(x => x.Id == id);
            if (video == null)
                return NotFound();

            var product = await GetProductAsync(model.ProductId);
            ValidateVideoForm(model, product, out var videoId);

            if (!ModelState.IsValid || product == null)
                return View(await PrepareFormAsync(model));

            if (video.ProductId != product.Id)
            {
                var slugBase = BuildSlugBase(product, model);
                video.Slug = await CreateUniqueSlugAsync(slugBase, video.Id);
            }

            video.ProductId = product.Id;
            video.YouTubeVideoId = videoId;
            video.YouTubeUrl = YouTubeVideoHelper.NormalizeWatchUrl(videoId);
            video.Title = CleanOptional(model.Title);
            video.VideoType = CleanOptional(model.VideoType) ?? "Tanıtım";
            video.SortOrder = model.SortOrder;
            video.IsFeatured = model.IsFeatured;
            video.IsActive = model.IsActive;
            video.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();
            var queued = await _translationQueue.QueueVideoAsync(video, HttpContext.RequestAborted);

            TempData["Success"] = $"Video bilgileri güncellendi. {queued} dil çevirisi kuyruğa alındı.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Toggle/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id)
        {
            var video = await _context.ProductVideos.FirstOrDefaultAsync(x => x.Id == id);
            if (video == null)
                return NotFound();

            video.IsActive = !video.IsActive;
            video.UpdatedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            TempData["Success"] = video.IsActive ? "Video yayına alındı." : "Video yayından kaldırıldı.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("Delete/{id:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var video = await _context.ProductVideos.FirstOrDefaultAsync(x => x.Id == id);
            if (video == null)
                return NotFound();

            _context.ProductVideos.Remove(video);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Video kaydı silindi. YouTube videosuna dokunulmadı.";
            return RedirectToAction(nameof(Index));
        }

        private async Task<ProductVideoFormViewModel> PrepareFormAsync(ProductVideoFormViewModel model)
        {
            model.Products = await _context.Products
                .AsNoTracking()
                .Where(x => !x.IsDeleted)
                .OrderByDescending(x => x.IsActive)
                .ThenBy(x => x.Name)
                .ToListAsync();

            return model;
        }

        private async Task<Product?> GetProductAsync(int productId)
        {
            return await _context.Products
                .FirstOrDefaultAsync(x => x.Id == productId && !x.IsDeleted);
        }

        private void ValidateVideoForm(ProductVideoFormViewModel model, Product? product, out string videoId)
        {
            videoId = string.Empty;

            if (product == null)
                ModelState.AddModelError(nameof(model.ProductId), "Seçilen program bulunamadı.");

            if (!YouTubeVideoHelper.TryExtractVideoId(model.YouTubeUrl, out videoId))
            {
                ModelState.AddModelError(
                    nameof(model.YouTubeUrl),
                    "Geçerli bir YouTube bağlantısı girin. youtu.be, watch, shorts, live ve embed bağlantıları desteklenir.");
            }
        }

        private static string BuildSlugBase(Product product, ProductVideoFormViewModel model)
        {
            var productSlug = !string.IsNullOrWhiteSpace(product.Slug)
                ? product.Slug
                : YouTubeVideoHelper.CreateSlug(product.Name, $"program-{product.Id}");

            var label = CleanOptional(model.Title)
                ?? CleanOptional(model.VideoType)
                ?? "video";

            return YouTubeVideoHelper.CreateSlug($"{productSlug}-{label}", $"program-{product.Id}-video");
        }

        private async Task<string> CreateUniqueSlugAsync(string slugBase, int? excludingId = null)
        {
            var slug = slugBase;
            var suffix = 2;

            while (await _context.ProductVideos.AnyAsync(x =>
                       x.Slug == slug && (!excludingId.HasValue || x.Id != excludingId.Value)))
            {
                slug = $"{slugBase}-{suffix++}";
            }

            return slug;
        }

        private static string? CleanOptional(string? value)
        {
            var cleaned = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }
    }
}
