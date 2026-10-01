using System.Text.Json;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed class CaritakipNotificationOutboxWorker(CaritakipNotificationOutboxStore store,
    CaritakipMobileNotificationDeliverer deliverer, CaritakipExpoPushService expo,
    CaritakipNativeMobileStore nativeStore, ILogger<CaritakipNotificationOutboxWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunLaneAsync("inbox",stoppingToken), RunLaneAsync("push",stoppingToken),
        RunLaneAsync("push",stoppingToken), RunLaneAsync("push",stoppingToken));

    private async Task RunLaneAsync(string lane,CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await store.ClaimAsync(stoppingToken,lane);
                if (job is null) { await Task.Delay(1000, stoppingToken); continue; }
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bildirim outbox döngüsü yeniden denenecek.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    public async Task ProcessAsync(NotificationJob job, CancellationToken ct)
    {
        try
        {
            if (job.Kind == "inbox")
            {
                var draft = JsonSerializer.Deserialize<NotificationDraft>(job.Payload) ?? throw new JsonException();
                await deliverer.DeliverAsync(draft.TenantId, draft.ExcludeDeviceId, draft.Category,
                    draft.Title, draft.Body, draft.Data, true, ct, job.Id);
                await store.CompleteAsync(job, ct);
                return;
            }
            var push = JsonSerializer.Deserialize<PushDraft>(job.Payload) ?? throw new JsonException();
            if (job.Kind == "push" && !expo.IsEnabled) { await store.RetryAsync(job,ct,"PushDisabled",TimeSpan.FromMinutes(5)); return; }
            if (job.Kind == "push" && !await nativeStore.IsPushTargetEligibleAsync(push.TenantId,push.DeviceId,push.Message.To,
                    push.Message.Data?.GetValueOrDefault("category") ?? "system",ct,push.Message.Data?.GetValueOrDefault("messageId")))
            { await store.CompleteAsync(job,ct); return; }
            var result = job.Kind == "receipt"
                ? await expo.GetReceiptAsync(push.ReceiptId!, ct)
                : await expo.SendOneAsync(push.Message, ct);
            if (result.Error == "DeviceNotRegistered")
            {
                await nativeStore.RemovePushTokenAsync(push.TenantId,push.DeviceId,push.Message.To,ct);
                await store.CompleteAsync(job,ct,true,result.Error);
            }
            else if (result.Error is "MessageTooBig" or "InvalidCredentials" or "MismatchSenderId")
            {
                logger.LogError("Expo kalıcı gönderim hatası job={Job} error={Error}",job.Id,result.Error);
                await store.CompleteAsync(job,ct,true,result.Error);
            }
            else if (!result.Ok)
            {
                // An absent receipt must never trigger blind re-sending of an accepted push.
                if (job.Kind == "receipt" && result.Error != "MessageRateExceeded" &&
                    push.AcceptedAtUtc < DateTime.UtcNow.AddHours(-23))
                {
                    logger.LogError("Expo receipt bulunamadı job={Job}; kabul edilmiş bildirim tekrar gönderilmedi.",job.Id);
                    await store.CompleteAsync(job,ct,true,"ReceiptExpired");
                }
                else await store.RetryAsync(job,ct,result.Error ?? "ExpoTransient",
                    job.Kind == "receipt" ? TimeSpan.FromMinutes(5) : null,
                    job.Kind == "receipt" && result.Error == "MessageRateExceeded" ? "push" : null,
                    job.Kind == "receipt" && result.Error == "MessageRateExceeded" ? push with { ReceiptId=null,AcceptedAtUtc=null } : null);
            }
            else if (job.Kind == "push")
                await store.RetryAsync(job,ct,"AwaitingReceipt",TimeSpan.FromMinutes(15),"receipt",
                    push with { ReceiptId=result.ReceiptId,AcceptedAtUtc=DateTime.UtcNow });
            else await store.CompleteAsync(job,ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is not null && (int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500 && (int)ex.StatusCode != 429)
        {
            logger.LogError("Expo istek hatası job={Job} status={Status}",job.Id,(int)ex.StatusCode);
            await store.CompleteAsync(job,ct,true,"ExpoHttp"+(int)ex.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,"Bildirim işi yeniden denenecek job={Job}",job.Id);
            await store.RetryAsync(job,ct,ex.GetType().Name);
        }
    }
}
