using System.Net;
using System.Net.Mail;
using System.Text;

namespace NSYazilim.Web.Services
{
    public interface IEmailSender
    {
        Task SendEmailAsync(string toEmail, string subject, string htmlBody);
    }

    public class SmtpEmailSender : IEmailSender
    {
        private readonly IConfiguration _configuration;

        public SmtpEmailSender(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
        {
            var host = GetSetting("Host");
            var portText = GetSetting("Port");
            var userName = GetSetting("UserName");
            var password = NormalizePassword(GetSetting("Password"), host);
            var fromEmail = GetSetting("FromEmail");
            var fromName = GetSetting("FromName");

            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("EmailSettings:Host boş.");

            if (string.IsNullOrWhiteSpace(fromEmail))
                fromEmail = userName;

            if (string.IsNullOrWhiteSpace(fromEmail))
                throw new InvalidOperationException("EmailSettings:FromEmail veya EmailSettings:UserName boş.");

            if (string.IsNullOrWhiteSpace(userName))
                throw new InvalidOperationException("EmailSettings:UserName boş.");

            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("EmailSettings:Password boş.");

            var port = int.TryParse(portText, out var parsedPort) ? parsedPort : 587;

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true,
                UseDefaultCredentials = false,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 20000,
                Credentials = new NetworkCredential(userName.Trim(), password)
            };

            using var message = new MailMessage
            {
                From = new MailAddress(fromEmail, string.IsNullOrWhiteSpace(fromName) ? "NSX Yazılım" : fromName, Encoding.UTF8),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8
            };

            message.To.Add(new MailAddress(toEmail));

            try
            {
                await client.SendMailAsync(message);
            }
            catch (SmtpException ex)
            {
                throw new InvalidOperationException($"SMTP gönderimi başarısız: {ex.StatusCode} - {GetDeepMessage(ex)}", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SMTP gönderimi başarısız: {GetDeepMessage(ex)}", ex);
            }
        }

        private string? GetSetting(string key)
        {
            return _configuration[$"EmailSettings:{key}"]
                ?? _configuration[$"MailSettings:{key}"];
        }

        private static string? NormalizePassword(string? password, string? host)
        {
            if (string.IsNullOrWhiteSpace(password))
                return password;

            if (!string.IsNullOrWhiteSpace(host) && host.Contains("gmail", StringComparison.OrdinalIgnoreCase))
                return string.Concat(password.Where(ch => !char.IsWhiteSpace(ch)));

            return password;
        }

        private static string GetDeepMessage(Exception exception)
        {
            var messages = new List<string>();
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message))
                    messages.Add(current.Message);
            }

            return string.Join(" | ", messages.Distinct());
        }
    }
}
