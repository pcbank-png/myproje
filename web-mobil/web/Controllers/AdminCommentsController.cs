using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("Admin")]
    public class AdminCommentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AdminCommentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("Comments")]
        public async Task<IActionResult> Comments(string status = "all")
        {
            ViewBag.Status = status;

            var query = _context.ProductComments
                .Include(x => x.Product)
                .Include(x => x.User)
                .Where(x => !x.IsDeleted);

            if (status == "pending")
                query = query.Where(x => !x.IsApproved);

            if (status == "approved")
                query = query.Where(x => x.IsApproved);

            var comments = await query
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            return View("~/Views/Admin/Comments.cshtml", comments);
        }

        [HttpPost("CommentApprove")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CommentApprove(int id)
        {
            var comment = await _context.ProductComments.FindAsync(id);

            if (comment == null)
            {
                TempData["Error"] = "Yorum bulunamadı.";
                return RedirectToAction(nameof(Comments));
            }

            comment.IsApproved = true;
            await _context.SaveChangesAsync();

            TempData["Success"] = "Yorum onaylandı.";
            return RedirectToAction(nameof(Comments), new { status = "pending" });
        }

        [HttpPost("CommentPassive")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CommentPassive(int id)
        {
            var comment = await _context.ProductComments.FindAsync(id);

            if (comment == null)
            {
                TempData["Error"] = "Yorum bulunamadı.";
                return RedirectToAction(nameof(Comments));
            }

            comment.IsApproved = false;
            await _context.SaveChangesAsync();

            TempData["Success"] = "Yorum pasife alındı.";
            return RedirectToAction(nameof(Comments));
        }

        [HttpPost("CommentDelete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CommentDelete(int id)
        {
            var comment = await _context.ProductComments.FindAsync(id);

            if (comment == null)
            {
                TempData["Error"] = "Yorum bulunamadı.";
                return RedirectToAction(nameof(Comments));
            }

            comment.IsDeleted = true;
            comment.IsApproved = false;
            await _context.SaveChangesAsync();

            TempData["Success"] = "Yorum silindi.";
            return RedirectToAction(nameof(Comments));
        }
    }
}
