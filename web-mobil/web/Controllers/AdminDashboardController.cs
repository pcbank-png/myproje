using Microsoft.AspNetCore.Mvc;

namespace NSYazilim.Web.Controllers
{
    // Admin dashboard artık tek merkezden AdminController.Index ile yönetiliyor.
    // Bu sınıf özellikle route çakışması üretmesin diye controller olarak devre dışı bırakıldı.
    [NonController]
    public class AdminDashboardController : Controller
    {
    }
}
