
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NSYazilim.Web.Controllers
{
    [Authorize]
    public class DownloadController : Controller
    {
        public IActionResult DownloadProtected()
        {
            return Content("Demo indiriliyor...");
        }
    }
}
