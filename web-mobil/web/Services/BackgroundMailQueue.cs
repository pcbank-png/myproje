using System.Threading.Channels;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed record BackgroundMailMessage(
        string ToEmail,
        string ToName,
        string Subject,
        string HtmlBody,
        string MailType,
        string LogBody);

    public sealed class BackgroundMailQueue
    {
        private readonly Channel<BackgroundMailMessage> _channel = Channel.CreateUnbounded<BackgroundMailMessage>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        public bool TryQueue(BackgroundMailMessage message)
            => _channel.Writer.TryWrite(message);

        public IAsyncEnumerable<BackgroundMailMessage> ReadAllAsync(CancellationToken cancellationToken)
            => _channel.Reader.ReadAllAsync(cancellationToken);
    }

    public sealed class BackgroundMailWorker : BackgroundService
    {
        private readonly BackgroundMailQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BackgroundMailWorker> _logger;

        public BackgroundMailWorker(
            BackgroundMailQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<BackgroundMailWorker> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var message in _queue.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                        var mailLog = new MailLog
                        {
                            ToEmail = string.IsNullOrWhiteSpace(message.ToEmail) ? "E-posta bulunamadı" : message.ToEmail.Trim(),
                            ToName = message.ToName,
                            Subject = message.Subject,
                            Body = message.LogBody,
                            MailType = message.MailType,
                            CreatedAt = DateTime.Now
                        };

                        try
                        {
                            if (string.IsNullOrWhiteSpace(message.ToEmail))
                                throw new InvalidOperationException("Kullanıcının e-posta adresi bulunamadı.");

                            var maxAttempts = string.Equals(message.MailType, "ShopierLicenseDelivery", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
                            Exception? lastError = null;

                            for (var attempt = 1; attempt <= maxAttempts; attempt++)
                            {
                                try
                                {
                                    await sender.SendEmailAsync(message.ToEmail.Trim(), message.Subject, message.HtmlBody);
                                    lastError = null;
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    lastError = ex;
                                    if (attempt < maxAttempts)
                                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), stoppingToken);
                                }
                            }

                            if (lastError != null)
                                throw lastError;

                            mailLog.IsSuccess = true;
                        }
                        catch (Exception ex)
                        {
                            mailLog.IsSuccess = false;
                            mailLog.ErrorMessage = ex.Message;
                            _logger.LogWarning(ex, "Background e-posta gönderilemedi. Tür: {MailType}, Alıcı: {ToEmail}", message.MailType, message.ToEmail);
                        }

                        try
                        {
                            db.MailLogs.Add(mailLog);
                            await db.SaveChangesAsync(stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Background e-posta logu kaydedilemedi. Tür: {MailType}", message.MailType);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Background e-posta kuyruğu işi tamamlanamadı.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal application shutdown.
            }
        }
    }
}
