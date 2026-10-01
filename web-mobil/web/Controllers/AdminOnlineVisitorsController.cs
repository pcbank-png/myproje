using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("Admin/OnlineVisitors")]
    public sealed class AdminOnlineVisitorsController : Controller
    {
        private readonly OnlineVisitorTracker _tracker;

        public AdminOnlineVisitorsController(OnlineVisitorTracker tracker)
        {
            _tracker = tracker;
        }

        [HttpGet("")]
        public IActionResult Index()
        {
            return View("~/Views/Admin/OnlineVisitors.cshtml");
        }

        [HttpGet("data")]
        public async Task<IActionResult> Data(CancellationToken cancellationToken)
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            return Json(await _tracker.GetSnapshotAsync(cancellationToken));
        }
    }
}
