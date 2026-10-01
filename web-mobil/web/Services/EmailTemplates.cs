using NSYazilim.Web.Models;
using System.Net;
using System.Text;

namespace NSYazilim.Web.Services
{
    public static class EmailTemplates
    {
        private const string BrandOrange = "#f56800";
        private const string BrandOrangeDark = "#d95500";
        private const string BrandDark = "#3a3d46";
        private const string BrandDarkSoft = "#4b4f59";
        private const string PageBg = "#f3f4f6";
        private const string CardBg = "#ffffff";
        private const string SoftBg = "#f8f8f8";
        private const string SoftOrangeBg = "#fff4ec";
        private const string Border = "#e5e7eb";
        private const string TextMain = "#1f2937";
        private const string TextMuted = "#6b7280";

        public static string Welcome(string fullName, string siteUrl, string? languageCode = null)
        {
            fullName = WebUtility.HtmlEncode(fullName);
            siteUrl = (siteUrl ?? string.Empty).TrimEnd('/');
            var isTurkish = IsTurkishLanguage(languageCode);
            var accountUrl = AppendUiLanguage($"{siteUrl}/Account/MyAccount", languageCode);

            if (!isTurkish)
            {
                var englishContent = $@"
                {Paragraph($"Hello <strong>{fullName}</strong>,")}
                {Paragraph("Your NSX Software customer account has been created successfully. You can now manage your orders, licenses and download files from one place.")}
                {InfoPanel("What you can do with your account",
                    "✓ Purchase software<br/>✓ View license keys<br/>✓ Download program files<br/>✓ Review order history")}
                {Button(accountUrl, "Go to My Account")}";

                return Layout("Welcome to NSX Software", englishContent, "Your account is ready", languageCode);
            }

            var content = $@"
                {Paragraph($"Merhaba <strong>{fullName}</strong>,")}
                {Paragraph("NSX Yazılım müşteri hesabınız başarıyla oluşturuldu. Artık siparişlerinizi, lisanslarınızı ve indirme dosyalarınızı tek merkezden takip edebilirsiniz.")}
                {InfoPanel("Hesabınızla yapabilecekleriniz",
                    "✓ Ürün satın alma<br/>✓ Lisans anahtarlarını görüntüleme<br/>✓ Program dosyalarını indirme<br/>✓ Sipariş geçmişini takip etme")}
                {Button(accountUrl, "Hesabıma Git")}";

            return Layout("NSX Yazılım’a hoş geldiniz", content, "Hesabınız hazır", languageCode);
        }

        public static string ResetPassword(string fullName, string resetUrl, string? languageCode = null)
        {
            fullName = WebUtility.HtmlEncode(fullName);
            var isTurkish = IsTurkishLanguage(languageCode);

            if (!isTurkish)
            {
                var englishContent = $@"
                {Paragraph($"Hello <strong>{fullName}</strong>,")}
                {Paragraph("Click the button below to reset your password. For security, this link is valid for 1 hour.")}
                {Button(resetUrl, "Reset My Password")}
                {SmallNote("If you did not request this action, you can safely ignore this email.")}";

                return Layout("Password reset request", englishContent, "Secure link", languageCode);
            }

            var content = $@"
                {Paragraph($"Merhaba <strong>{fullName}</strong>,")}
                {Paragraph("Şifrenizi yenilemek için aşağıdaki butona tıklayın. Bu bağlantı güvenlik nedeniyle 1 saat geçerlidir.")}
                {Button(resetUrl, "Şifremi Yenile")}
                {SmallNote("Bu işlemi siz başlatmadıysanız bu e-postayı yok sayabilirsiniz.")}";

            return Layout("Şifre sıfırlama isteği", content, "Güvenli bağlantı", languageCode);
        }

        public static string OrderCompleted(Order order, string siteUrl)
        {
            siteUrl = (siteUrl ?? string.Empty).TrimEnd('/');

            var customerName = WebUtility.HtmlEncode(order.User?.FullName ?? "Değerli müşterimiz");
            var orderNumber = WebUtility.HtmlEncode(order.OrderNumber);
            var orderDate = order.CreatedAt.ToString("dd.MM.yyyy HH:mm");
            var total = order.TotalAmount.ToString("N2");

            var productRows = BuildProductRows(order);
            var licenseRows = BuildLicenseRows(order);

            var content = $@"
                {Paragraph($"Merhaba <strong>{customerName}</strong>,")}
                {Paragraph("NSX Yazılım siparişiniz başarıyla tamamlandı. Alışveriş detaylarınız ve lisans anahtarlarınız aşağıdadır.")}
                {InfoPanel("Sipariş Bilgileri",
                    $"<strong>Sipariş No:</strong> {orderNumber}<br/><strong>Tarih:</strong> {orderDate}<br/><strong>Ödeme Durumu:</strong> Ödendi<br/><strong>Toplam:</strong> {total} TL")}
                {SectionTitle("Alışveriş Detayı")}
                {ProductTable(productRows)}
                {SectionTitle("Lisans Anahtarları")}
                {LicenseTable(licenseRows)}
                {HighlightBox("Lisans anahtarlarınızı hesabınızdaki <strong>Lisanslarım</strong> bölümünden görüntüleyebilirsiniz. Programınız siteye ulaşamadığında ayrı kurtarma kodu istemeden yerel Offline Lisans Modu ile çalışmaya devam edecek şekilde tasarlanmıştır.")}
                {Button($"{siteUrl}/Account/Licenses", "Lisanslarıma Git")}";

            return Layout("Siparişiniz ve lisans bilgileriniz hazır", content, "Ödeme başarılı");
        }

        public static string FreeLicenseDelivery(
            string fullName,
            string productName,
            string licenseKey,
            string siteUrl,
            string? languageCode = null)
        {
            var isTurkish = IsTurkishLanguage(languageCode);
            fullName = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(fullName)
                ? (isTurkish ? "Değerli müşterimiz" : "Valued customer")
                : fullName);
            productName = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(productName)
                ? (isTurkish ? "NSX Yazılım Ürünü" : "NSX Software Product")
                : productName);
            licenseKey = WebUtility.HtmlEncode(licenseKey ?? string.Empty);
            siteUrl = (siteUrl ?? string.Empty).TrimEnd('/');
            var licensesUrl = AppendUiLanguage($"{siteUrl}/Account/Licenses", languageCode);

            if (!isTurkish)
            {
                var englishContent = $@"
                {Paragraph($"Hello <strong>{fullName}</strong>,")}
                {Paragraph("Your free software download is complete. Your license code for activation is shown below.")}
                {InfoPanel("License information",
                    $"<strong>Product:</strong> {productName}<br/><strong>License type:</strong> Lifetime Free<br/><strong>Device allowance:</strong> 1 device")}
                {SectionTitle("Your License Code")}
                <div style='background:{BrandDark};color:#ffffff;padding:16px 18px;border-radius:12px;font-family:Consolas,Monaco,monospace;font-size:17px;line-height:1.6;letter-spacing:.7px;word-break:break-all;margin:0 0 18px;'>{licenseKey}</div>
                {HighlightBox("Enter this code on the software's <strong>License Activation</strong> screen to pair the product with your device. Advertising remains enabled in the free edition.")}
                {Button(licensesUrl, "Go to My Licenses")}";

                return Layout("Your free license code is ready", englishContent, "Free software license", languageCode);
            }

            var content = $@"
                {Paragraph($"Merhaba <strong>{fullName}</strong>,")}
                {Paragraph("Ücretsiz ürün indirme işleminiz tamamlandı. Programı etkinleştirmek için kullanacağınız lisans kodu aşağıdadır.")}
                {InfoPanel("Lisans Bilgileri",
                    $"<strong>Ürün:</strong> {productName}<br/><strong>Lisans Türü:</strong> Ömür Boyu Ücretsiz<br/><strong>Cihaz Hakkı:</strong> 1 cihaz")}
                {SectionTitle("Lisans Kodunuz")}
                <div style='background:{BrandDark};color:#ffffff;padding:16px 18px;border-radius:12px;font-family:Consolas,Monaco,monospace;font-size:17px;line-height:1.6;letter-spacing:.7px;word-break:break-all;margin:0 0 18px;'>{licenseKey}</div>
                {HighlightBox("Programdaki <strong>Lisans Etkinleştirme</strong> ekranına bu kodu girerek ürünü cihazınızla eşleştirebilirsiniz. Ücretsiz sürümde reklam gösterimi devam eder.")}
                {Button(licensesUrl, "Lisanslarıma Git")}";

            return Layout("Ücretsiz lisans kodunuz hazır", content, "Ücretsiz ürün lisansı", languageCode);
        }

        public static string LicenseRecoveryCode(License license, string recoveryCode, string siteUrl)
        {
            siteUrl = (siteUrl ?? string.Empty).TrimEnd('/');
            var customerName = WebUtility.HtmlEncode(license.User?.FullName ?? "Değerli müşterimiz");
            var productName = WebUtility.HtmlEncode(license.Product?.Name ?? "NSX Yazılım Ürünü");
            var licenseKey = WebUtility.HtmlEncode(license.LicenseKey);
            var licenseType = license.LicenseType == "Lifetime" ? "Sınırsız Lisans" : "Yıllık Lisans";
            var expire = license.EndDate == null ? "Süresiz" : license.EndDate.Value.ToString("dd.MM.yyyy");
            var machine = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(license.MachineId) ? "Eşleşmemiş" : license.MachineId);
            var safeCode = WebUtility.HtmlEncode(recoveryCode ?? string.Empty);

            var content = $@"
                {Paragraph($"Merhaba <strong>{customerName}</strong>,")}
                {Paragraph("Lisansınıza ait kurtarma lisans kodu aşağıdadır. Bu kod, web lisans sunucusuna ulaşılamadığı durumlarda aynı cihazda lisansı yeniden etkinleştirmek için saklanmalıdır.")}
                {InfoPanel("Lisans Bilgileri",
                    $"<strong>Ürün:</strong> {productName}<br/><strong>Lisans:</strong> {licenseType}<br/><strong>Bitiş:</strong> {expire}<br/><strong>Makine Kodu:</strong> <span style='font-family:Consolas,Monaco,monospace;'>{machine}</span><br/><strong>Lisans Anahtarı:</strong> <span style='font-family:Consolas,Monaco,monospace;'>{licenseKey}</span>")}
                {HighlightBox("<strong>Önemli:</strong> Kurtarma kodunuzu güvenli bir yerde saklayın. Kurtarma kodu, yerel veritabanı yedeğiniz ve lisans sunucusuna erişim yoksa lisansınız aynı cihazda tekrar aktif edilemeyebilir. Kod cihazınıza ve lisansınıza özel olduğu için farklı bilgisayarda çalışması beklenmez.")}
                {SectionTitle("Kurtarma Lisans Kodu")}
                <div style='background:#111827;color:#ffffff;padding:16px;border-radius:12px;font-family:Consolas,Monaco,monospace;font-size:12px;line-height:1.6;word-break:break-all;white-space:pre-wrap;margin:0 0 18px;'>{safeCode}</div>
                {SmallNote("Bu kodu e-postanızda, harici diskte veya güvenli bir not alanında saklamanız önerilir.")}
                {Button($"{siteUrl}/Account/Licenses", "Lisanslarıma Git")}";

            return Layout("Kurtarma lisans kodunuz", content, "Lisans güvenliği");
        }

        public static string FooterContactMessage(string senderEmail, string message)
        {
            var safeMail = WebUtility.HtmlEncode(senderEmail);
            var safeMessage = WebUtility.HtmlEncode(message)
                .Replace("\r\n", "<br />")
                .Replace("\n", "<br />");

            var content = $@"
                {Paragraph("Web sitesinin footer iletişim formundan yeni bir mesaj geldi.")}
                {InfoPanel("Gönderen Mail", $"<a href='mailto:{safeMail}' style='color:{BrandOrange};text-decoration:none;font-weight:700;'>{safeMail}</a>")}
                {InfoPanel("Mesaj", $"<span style='color:{TextMain};'>{safeMessage}</span>")}";

            return Layout("NSX Footer İletişim Mesajı", content, "Yeni iletişim mesajı");
        }

        private static string BuildProductRows(Order order)
        {
            if (order.OrderItems == null || !order.OrderItems.Any())
                return "<tr><td colspan='4' style='padding:14px;color:#6b7280;font-family:Arial,Helvetica,sans-serif;'>Ürün bilgisi bulunamadı.</td></tr>";

            var sb = new StringBuilder();

            foreach (var item in order.OrderItems)
            {
                var productName = WebUtility.HtmlEncode(item.Product?.Name ?? $"Ürün #{item.ProductId}");
                var licenseType = item.LicenseType == "Lifetime" ? "Sınırsız" : "1 Yıllık";
                var quantity = item.Quantity < 1 ? 1 : item.Quantity;
                var lineTotal = (item.UnitPrice * quantity).ToString("N2");

                sb.Append($@"
                    <tr>
                        <td style='padding:12px;border-bottom:1px solid {Border};color:{TextMain};font-size:14px;font-family:Arial,Helvetica,sans-serif;'>{productName}</td>
                        <td align='center' style='padding:12px;border-bottom:1px solid {Border};color:{TextMain};font-size:14px;font-family:Arial,Helvetica,sans-serif;'>{licenseType}</td>
                        <td align='center' style='padding:12px;border-bottom:1px solid {Border};color:{TextMain};font-size:14px;font-family:Arial,Helvetica,sans-serif;'>{quantity}</td>
                        <td align='right' style='padding:12px;border-bottom:1px solid {Border};color:{BrandDark};font-size:14px;font-weight:700;font-family:Arial,Helvetica,sans-serif;'>{lineTotal} TL</td>
                    </tr>");
            }

            return sb.ToString();
        }

        private static string BuildLicenseRows(Order order)
        {
            if (order.Licenses == null || !order.Licenses.Any())
                return "<tr><td style='padding:14px;color:#6b7280;font-family:Arial,Helvetica,sans-serif;'>Lisans bilgisi bulunamadı.</td></tr>";

            var sb = new StringBuilder();

            foreach (var license in order.Licenses.OrderBy(x => x.Product?.Name).ThenBy(x => x.Id))
            {
                var productName = WebUtility.HtmlEncode(license.Product?.Name ?? $"Ürün #{license.ProductId}");
                var key = WebUtility.HtmlEncode(license.LicenseKey);
                var licenseType = license.LicenseType == "Lifetime" ? "Sınırsız Lisans" : "1 Yıllık Lisans";
                var expire = license.EndDate == null ? "Süresiz" : license.EndDate.Value.ToString("dd.MM.yyyy");

                sb.Append($@"
                    <tr>
                        <td style='padding:14px;border-bottom:1px solid {Border};font-family:Arial,Helvetica,sans-serif;'>
                            <div style='font-size:13px;color:{TextMuted};margin-bottom:8px;'>{productName} · {licenseType} · Bitiş: {expire}</div>
                            <div style='background:{BrandDark};color:#ffffff;padding:12px 14px;font-size:14px;font-family:Consolas,Monaco,monospace;letter-spacing:.4px;border-radius:8px;'>{key}</div>
                        </td>
                    </tr>");
            }

            return sb.ToString();
        }

        private static string Layout(string title, string content, string badgeText, string? languageCode = null)
        {
            title = WebUtility.HtmlEncode(title);
            badgeText = WebUtility.HtmlEncode(badgeText);
            var isTurkish = IsTurkishLanguage(languageCode);
            var htmlLanguage = isTurkish ? "tr" : "en";
            var brandName = isTurkish ? "NSX Yazılım" : "NSX Software";
            var footerLine1 = isTurkish ? "NSX Yazılım · Gerçekçi çözümler sunar." : "NSX Software · Practical software solutions.";
            var footerLine2 = isTurkish
                ? "Bu e-posta sipariş, üyelik ve lisans bilgilendirmesi amacıyla gönderilmiştir."
                : "This email was sent for account, order or license information.";

            return $@"
<!doctype html>
<html lang='{htmlLanguage}'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <meta http-equiv='X-UA-Compatible' content='IE=edge'>
    <title>{title}</title>
</head>
<body style='margin:0;padding:0;background:{PageBg};'>
    <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='background:{PageBg};margin:0;padding:0;border-collapse:collapse;'>
        <tr>
            <td align='center' style='padding:24px 12px;'>
                <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='max-width:680px;border-collapse:collapse;'>
                    <tr>
                        <td style='background:{BrandDark};padding:26px 30px;border-radius:22px 22px 0 0;'>
                            <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:collapse;'>
                                <tr>
                                    <td align='left'>
                                        <table role='presentation' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;margin-bottom:16px;'>
                                            <tr>
                                                <td style='background:{BrandOrange};color:#ffffff;font-family:Arial,Helvetica,sans-serif;font-size:13px;font-weight:700;padding:8px 14px;border-radius:999px;'>{brandName}</td>
                                            </tr>
                                        </table>
                                        <div style='font-family:Arial,Helvetica,sans-serif;font-size:32px;line-height:1.2;font-weight:700;color:#ffffff;margin:0 0 10px;'>{title}</div>
                                        <div style='font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.6;color:#e5e7eb;margin:0;'>{badgeText}</div>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                    <tr>
                        <td style='background:{CardBg};border:1px solid {Border};border-top:0;padding:30px;border-radius:0 0 22px 22px;'>
                            {content}
                            <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:collapse;margin-top:28px;'>
                                <tr>
                                    <td style='border-top:1px solid {Border};font-size:0;line-height:0;'>&nbsp;</td>
                                </tr>
                                <tr>
                                    <td style='padding-top:18px;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:1.7;color:{TextMuted};'>
                                        {footerLine1}<br/>
                                        {footerLine2}
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

        private static bool IsTurkishLanguage(string? languageCode)
            => string.IsNullOrWhiteSpace(languageCode)
                || languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase);

        private static string AppendUiLanguage(string url, string? languageCode)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(languageCode))
                return url;

            var separator = url.Contains('?') ? "&" : "?";
            return $"{url}{separator}uiLang={Uri.EscapeDataString(languageCode.Trim())}";
        }

        private static string Paragraph(string html)
            => $"<p style='margin:0 0 18px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.8;color:{TextMain};'>{html}</p>";

        private static string SmallNote(string html)
            => $"<p style='margin:18px 0 0;font-family:Arial,Helvetica,sans-serif;font-size:13px;line-height:1.7;color:{TextMuted};'>{html}</p>";

        private static string SectionTitle(string title)
            => $"<div style='margin:24px 0 12px;font-family:Arial,Helvetica,sans-serif;font-size:18px;font-weight:700;line-height:1.4;color:{BrandDark};'>{title}</div>";

        private static string InfoPanel(string title, string body)
            => $@"
<table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:separate;background:{SoftBg};border:1px solid {Border};border-radius:16px;margin:0 0 18px;'>
    <tr>
        <td style='padding:18px;font-family:Arial,Helvetica,sans-serif;'>
            <div style='font-size:15px;font-weight:700;line-height:1.5;color:{BrandDark};margin-bottom:8px;'>{title}</div>
            <div style='font-size:14px;line-height:1.8;color:{TextMain};'>{body}</div>
        </td>
    </tr>
</table>";

        private static string HighlightBox(string body)
            => $@"
<table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:separate;background:{SoftOrangeBg};border:1px solid #ffd2b3;border-radius:16px;margin:0 0 18px;'>
    <tr>
        <td style='padding:16px 18px;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.8;color:{BrandDark};'>{body}</td>
    </tr>
</table>";

        private static string ProductTable(string rows)
            => $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;border:1px solid {Border};margin:0 0 20px;'>
    <thead>
        <tr style='background:{BrandDark};'>
            <th align='left' style='padding:12px;color:#ffffff;font-size:13px;font-family:Arial,Helvetica,sans-serif;border-bottom:1px solid {Border};'>Ürün</th>
            <th align='center' style='padding:12px;color:#ffffff;font-size:13px;font-family:Arial,Helvetica,sans-serif;border-bottom:1px solid {Border};'>Lisans</th>
            <th align='center' style='padding:12px;color:#ffffff;font-size:13px;font-family:Arial,Helvetica,sans-serif;border-bottom:1px solid {Border};'>Adet</th>
            <th align='right' style='padding:12px;color:#ffffff;font-size:13px;font-family:Arial,Helvetica,sans-serif;border-bottom:1px solid {Border};'>Tutar</th>
        </tr>
    </thead>
    <tbody>{rows}</tbody>
</table>";

        private static string LicenseTable(string rows)
            => $@"
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;border:1px solid {Border};margin:0 0 20px;'>
    <tbody>{rows}</tbody>
</table>";

        private static string Button(string url, string text)
        {
            url = WebUtility.HtmlEncode(url);
            text = WebUtility.HtmlEncode(text);

            return $@"
<table role='presentation' cellpadding='0' cellspacing='0' border='0' style='border-collapse:collapse;margin:8px 0 0;'>
    <tr>
        <td align='center' bgcolor='{BrandOrange}' style='background:{BrandOrange};border-radius:12px;'>
            <a href='{url}' style='display:inline-block;padding:14px 22px;font-family:Arial,Helvetica,sans-serif;font-size:15px;font-weight:700;line-height:1.2;color:#ffffff;text-decoration:none;background:{BrandOrange};border-radius:12px;'>{text}</a>
        </td>
    </tr>
</table>";
        }
    }
}
