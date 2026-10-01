using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("Admin/VisitorStatistics")]
    public sealed class AdminVisitorStatisticsController : Controller
    {
        private readonly SiteVisitTrackingService _trackingService;

        public AdminVisitorStatisticsController(SiteVisitTrackingService trackingService)
        {
            _trackingService = trackingService;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index([FromQuery] int days = 30, CancellationToken cancellationToken = default)
        {
            var model = await _trackingService.BuildStatisticsAsync(days, cancellationToken);
            return View("~/Views/Admin/VisitorStatistics.cshtml", model);
        }
    }
}
