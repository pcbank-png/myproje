using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using NSYazilim.Web.ViewModels;
using System.Net;

namespace NSYazilim.Web.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("Admin")]
    public class AdminBulkMailController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailSender _emailSender;

        public AdminBulkMailController(ApplicationDbContext context, IEmailSender emailSender)
        {
            _context = context;
            _emailSender = emailSender;
        }

        [HttpGet("BulkMail")]
        public async Task<IActionResult> BulkMail()
        {
            var model = new BulkMailViewModel
            {
                LatestLogs = await GetLatestLogs()
            };

            return View("~/Views/Admin/BulkMail.cshtml", model);
        }

        [HttpPost("BulkMail")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkMail(BulkMailViewModel model)
        {
            if (!ModelState.IsValid)
            {
                model.LatestLogs = await GetLatestLogs();
                return View("~/Views/Admin/BulkMail.cshtml", model);
            }

            var usersQuery = _context.Users
                .Where(x => !x.IsDeleted && x.IsActive && x.Role == "User");

            if (model.TargetGroup == "licensed")
            {
                usersQuery = usersQuery.Where(x => _context.Licenses.Any(l => l.UserId == x.Id));
            }
            else if (model.TargetGroup == "ordered")
            {
                usersQuery = usersQuery.Where(x => _context.Orders.Any(o => o.UserId == x.Id));
            }

            var users = await usersQuery
                .Where(x => !string.IsNullOrWhiteSpace(x.Email))
                .OrderByDescending(x => x.Id)
                .ToListAsync();

            if (!users.Any())
            {
                TempData["Warning"] = "Seçilen alıcı grubunda e-posta gönderilecek kullanıcı bulunamadı.";
                model.LatestLogs = await GetLatestLogs();
                return View("~/Views/Admin/BulkMail.cshtml", model);
            }

            var sent = 0;
            var failed = 0;

            foreach (var user in users)
            {
                var body = model.SendAsHtml
                    ? BuildBulkMailTemplate(user.FullName, model.Message)
                    : WebUtility.HtmlEncode(model.Message).Replace("\n", "<br>");

                var log = new MailLog
                {
                    ToEmail = user.Email,
                    ToName = user.FullName,
                    Subject = model.Subject,
                    Body = model.Message,
                    MailType = "Bulk",
                    CreatedAt = DateTime.Now
                };

                Exception? lastError = null;
                var delivered = false;

                for (var attempt = 1; attempt <= 3 && !delivered; attempt++)
                {
                    try
                    {
                        await _emailSender.SendEmailAsync(user.Email, model.Subject, body);
                        delivered = true;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (attempt < 3)
                            await Task.Delay(TimeSpan.FromSeconds(attempt * 4));
                    }
                }

                if (delivered)
                {
                    log.IsSuccess = true;
                    sent++;
                }
                else
                {
                    log.IsSuccess = false;
                    log.ErrorMessage = lastError?.Message ?? "SMTP gönderimi başarısız.";
                    failed++;
                }

                _context.MailLogs.Add(log);
                await _context.SaveChangesAsync();

                // SMTP sağlayıcısının kısa süreli gönderim hız limitine takılmamak için
                // toplu gönderimler arasında kontrollü bekleme uygula.
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            TempData["Success"] = $"Toplu mail tamamlandı. Başarılı: {sent}, Hatalı: {failed}";

            model.SentCount = sent;
            model.FailedCount = failed;
            model.LatestLogs = await GetLatestLogs();

            return View("~/Views/Admin/BulkMail.cshtml", model);
        }


        [HttpPost("BulkMailBatch")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkMailBatch(BulkMailViewModel model, int offset = 0)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    ok = false,
                    message = "Toplu mail bilgileri geçersiz. Konu, mesaj ve alıcı grubunu kontrol edin."
                });
            }

            const int batchSize = 5;

            var usersQuery = _context.Users
                .Where(x => !x.IsDeleted && x.IsActive && x.Role == "User");

            if (model.TargetGroup == "licensed")
            {
                usersQuery = usersQuery.Where(x => _context.Licenses.Any(l => l.UserId == x.Id));
            }
            else if (model.TargetGroup == "ordered")
            {
                usersQuery = usersQuery.Where(x => _context.Orders.Any(o => o.UserId == x.Id));
            }

            usersQuery = usersQuery.Where(x => !string.IsNullOrWhiteSpace(x.Email));

            var total = await usersQuery.CountAsync();
            var safeOffset = Math.Max(0, offset);

            var users = await usersQuery
                .OrderByDescending(x => x.Id)
                .Skip(safeOffset)
                .Take(batchSize)
                .ToListAsync();

            if (!users.Any())
            {
                return Json(new
                {
                    ok = true,
                    done = true,
                    total,
                    nextOffset = safeOffset,
                    sent = 0,
                    failed = 0
                });
            }

            var sent = 0;
            var failed = 0;

            foreach (var user in users)
            {
                var body = model.SendAsHtml
                    ? BuildBulkMailTemplate(user.FullName, model.Message)
                    : WebUtility.HtmlEncode(model.Message).Replace("\n", "<br>");

                var log = new MailLog
                {
                    ToEmail = user.Email,
                    ToName = user.FullName,
                    Subject = model.Subject,
                    Body = model.Message,
                    MailType = "Bulk",
                    CreatedAt = DateTime.Now
                };

                Exception? lastError = null;
                var delivered = false;

                for (var attempt = 1; attempt <= 3 && !delivered; attempt++)
                {
                    try
                    {
                        await _emailSender.SendEmailAsync(user.Email, model.Subject, body);
                        delivered = true;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (attempt < 3)
                            await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                    }
                }

                if (delivered)
                {
                    log.IsSuccess = true;
                    sent++;
                }
                else
                {
                    log.IsSuccess = false;
                    log.ErrorMessage = lastError?.Message ?? "SMTP gönderimi başarısız.";
                    failed++;
                }

                _context.MailLogs.Add(log);
                await _context.SaveChangesAsync();

                // Büyük listelerde sağlayıcının kısa süreli hız limitine takılmamak için
                // her alıcı arasında kontrollü bekleme uygulanır. Gönderim küçük partilerle
                // ilerlediğinden tek HTTP isteği yüzlerce/1000+ alıcı boyunca açık kalmaz.
                await Task.Delay(TimeSpan.FromMilliseconds(1500));
            }

            var nextOffset = safeOffset + users.Count;

            return Json(new
            {
                ok = true,
                done = nextOffset >= total,
                total,
                nextOffset,
                sent,
                failed
            });
        }

        [HttpPost("BulkMailLogDelete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkMailLogDelete(int id)
        {
            var log = await _context.MailLogs.FirstOrDefaultAsync(x => x.Id == id && x.MailType == "Bulk");
            if (log == null)
            {
                TempData["Error"] = "Mail kaydı bulunamadı.";
                return RedirectToAction(nameof(BulkMail));
            }

            _context.MailLogs.Remove(log);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Mail kaydı silindi.";
            return RedirectToAction(nameof(BulkMail));
        }

        [HttpPost("BulkMailLogsClear")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkMailLogsClear()
        {
            var logs = await _context.MailLogs.Where(x => x.MailType == "Bulk").ToListAsync();
            if (logs.Any())
            {
                _context.MailLogs.RemoveRange(logs);
                await _context.SaveChangesAsync();
            }

            TempData["Success"] = "Toplu mail geçmişi temizlendi.";
            return RedirectToAction(nameof(BulkMail));
        }

        private async Task<List<BulkMailLogItem>> GetLatestLogs()
        {
            return await _context.MailLogs
                .Where(x => x.MailType == "Bulk")
                .OrderByDescending(x => x.Id)
                .Take(20)
                .Select(x => new BulkMailLogItem
                {
                    Id = x.Id,
                    ToEmail = x.ToEmail,
                    ToName = x.ToName,
                    Subject = x.Subject,
                    IsSuccess = x.IsSuccess,
                    ErrorMessage = x.ErrorMessage,
                    CreatedAt = x.CreatedAt
                })
                .ToListAsync();
        }

        private static string BuildBulkMailTemplate(string fullName, string message)
        {
            const string brandOrange = "#f56800";
            const string brandDark = "#3a3d46";
            const string pageBg = "#f3f4f6";
            const string cardBg = "#ffffff";
            const string softOrangeBg = "#fff4ec";
            const string border = "#e5e7eb";
            const string textMain = "#1f2937";
            const string textMuted = "#6b7280";

            fullName = WebUtility.HtmlEncode(
                string.IsNullOrWhiteSpace(fullName) ? "Değerli müşterimiz" : fullName);

            var safeMessage = WebUtility.HtmlEncode(message ?? string.Empty)
                .Replace("\r\n", "<br />")
                .Replace("\n", "<br />");

            return $@"
<!doctype html>
<html lang='tr'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <meta http-equiv='X-UA-Compatible' content='IE=edge'>
    <title>NSX Yazılım’dan mesajınız var</title>
</head>
<body style='margin:0;padding:0;background:{pageBg};'>
    <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='background:{pageBg};margin:0;padding:0;border-collapse:collapse;'>
        <tr>
            <td align='center' style='padding:24px 12px;'>
                <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='max-width:680px;border-collapse:collapse;'>
                    <tr>
                        <td style='background:{brandDark};padding:26px 30px;border-radius:22px 22px 0 0;'>
                            <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:collapse;'>
                                <tr>
                                    <td align='left'>
                                        <table role='presentation' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;margin-bottom:16px;'>
                                            <tr>
                                                <td style='background:{brandOrange};color:#ffffff;font-family:Arial,Helvetica,sans-serif;font-size:13px;font-weight:700;padding:8px 14px;border-radius:999px;'>NSX Yazılım</td>
                                            </tr>
                                        </table>
                                        <div style='font-family:Arial,Helvetica,sans-serif;font-size:32px;line-height:1.2;font-weight:700;color:#ffffff;margin:0 0 10px;'>NSX Yazılım’dan mesajınız var</div>
                                        <div style='font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.6;color:#e5e7eb;margin:0;'>Bilgilendirme ve duyuru</div>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    <tr>
                        <td style='background:{cardBg};border:1px solid {border};border-top:0;padding:30px;border-radius:0 0 22px 22px;'>
                            <p style='margin:0 0 18px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.8;color:{textMain};'>Merhaba <strong>{fullName}</strong>,</p>
                            <div style='margin:0 0 18px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.8;color:{textMain};'>{safeMessage}</div>

                            <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:separate;background:{softOrangeBg};border:1px solid #ffd2b3;border-radius:16px;margin:0 0 18px;'>
                                <tr>
                                    <td style='padding:16px 18px;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.8;color:{brandDark};'>
                                        NSX Yazılım hesabınızdan siparişlerinizi, lisanslarınızı ve indirme dosyalarınızı takip edebilirsiniz.
                                    </td>
                                </tr>
                            </table>

                            <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:collapse;margin-top:28px;'>
                                <tr>
                                    <td style='border-top:1px solid {border};font-size:0;line-height:0;'>&nbsp;</td>
                                </tr>
                                <tr>
                                    <td style='padding-top:18px;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:1.7;color:{textMuted};'>
                                        NSX Yazılım · Gerçekçi çözümler sunar.<br/>
                                        Bu e-posta bilgilendirme ve duyuru amacıyla gönderilmiştir.
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
        }
    }
}
