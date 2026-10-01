using Microsoft.AspNetCore.Mvc;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [ApiController]
    [Route("presence")]
    public sealed class PresenceController : ControllerBase
    {
        private readonly OnlineVisitorTracker _tracker;

        public PresenceController(OnlineVisitorTracker tracker)
        {
            _tracker = tracker;
        }

        [HttpPost("ping")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Ping([FromBody] OnlineVisitorPingRequest? request, CancellationToken cancellationToken)
        {
            if (!IsSameSiteRequest())
            {
                return BadRequest();
            }

            HttpContext.Session.SetString("NSX.OnlineVisitor", "1");

            request ??= new OnlineVisitorPingRequest();
            await _tracker.TouchAsync(HttpContext, request, cancellationToken);
            return NoContent();
        }

        private bool IsSameSiteRequest()
        {
            var origin = Request.Headers["Origin"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(origin))
            {
                return true;
            }

            return Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                && string.Equals(originUri.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase);
        }
    }
}
