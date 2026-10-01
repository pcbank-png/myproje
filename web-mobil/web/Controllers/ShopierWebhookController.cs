using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [ApiController]
    [Route("api/shopier/webhook")]
    public class ShopierWebhookController : ControllerBase
    {
        private readonly ShopierService _shopierService;
        private readonly ILogger<ShopierWebhookController> _logger;

        public ShopierWebhookController(ShopierService shopierService, ILogger<ShopierWebhookController> logger)
        {
            _shopierService = shopierService;
            _logger = logger;
        }

        [AllowAnonymous]
        [HttpPost]
        public async Task<IActionResult> Receive(CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(Request.Body);
            var rawBody = await reader.ReadToEndAsync(cancellationToken);
            var signature = Request.Headers["Shopier-Signature"].ToString();
            var eventName = Request.Headers["Shopier-Event"].ToString();
            var webhookId = Request.Headers["Shopier-Webhook-Id"].ToString();

            if (!_shopierService.IsWebhookConfigured)
            {
                _logger.LogWarning("Shopier webhook çağrısı alındı ancak webhook doğrulama anahtarı hazır değil.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (!_shopierService.VerifyWebhookSignature(rawBody, signature))
            {
                _logger.LogWarning("Geçersiz Shopier webhook imzası reddedildi. Event={EventName} WebhookId={WebhookId}", eventName, webhookId);
                return Unauthorized();
            }

            if (!string.Equals(eventName, "order.created", StringComparison.OrdinalIgnoreCase))
                return Ok(new { received = true, ignored = true });

            try
            {
                var saved = await _shopierService.ProcessOrderCreatedWebhookAsync(rawBody, webhookId, cancellationToken);
                if (!saved)
                    return BadRequest(new { received = false });

                return Ok(new { received = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shopier order.created webhook işlenemedi. WebhookId={WebhookId}", webhookId);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
