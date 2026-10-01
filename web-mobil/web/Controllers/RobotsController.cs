using Microsoft.AspNetCore.Mvc;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    public class RobotsController : Controller
    {
        [HttpGet("robots.txt")]
        public IActionResult Index()
        {
            var robots = $@"User-agent: *
Allow: /
Allow: /blog
Allow: /blog/
Allow: /blog/nsx-komisyonlu-cari-takip-pro
Allow: /urun/nsx-komisyonlu-cari-takip-pro
Allow: /ucretsiz-teknik-servis-programi
Allow: /ucretsiz-bilgisayar-hizlandirma-programi
Allow: /oto-tamir-programi
Allow: /oto-galeri-programi
Allow: /servis-live-programi
Allow: /klinik-programi
Allow: /randevu-programi
Allow: /assets/
Allow: /css/
Allow: /js/
Allow: /lib/
Allow: /uploads/products/
Allow: /uploads/advertisements/
Disallow: /admin/
Disallow: /Admin/
Disallow: /store/downloaddemo
Disallow: /store/downloadfree
Disallow: /account/downloadfile
Disallow: /Store/DownloadDemo
Disallow: /Store/DownloadFree
Disallow: /Account/DownloadFile
Disallow: /api/
Disallow: /*/admin/
Disallow: /*/Admin/
Disallow: /*/account/
Disallow: /*/Account/
Disallow: /*/store/downloaddemo
Disallow: /*/store/downloadfree
Disallow: /*/store/freeactivationpayload
Disallow: /*/store/freeprogramfile

Sitemap: {SeoTextHelper.Domain}/sitemap.xml
";
            return Content(robots, "text/plain; charset=utf-8");
        }
    }
}
