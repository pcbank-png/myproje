using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.SignalR;
using MySqlConnector;
using NSYazilim.Web.Services;
using NSYazilim.Web.CaritakipCloud.Services;
using NSYazilim.Web.CaritakipCloud.Models;
using NSYazilim.Web.CaritakipCloud.Hubs;

void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
var key=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
var builder=WebApplication.CreateBuilder();
builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["RemoteSupport:Operators:test:Key"]=key });
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options=>{
    options.Events.OnRedirectToLogin=context=>{context.Response.StatusCode=401;return Task.CompletedTask;};
    options.Events.OnRedirectToAccessDenied=context=>{context.Response.StatusCode=403;return Task.CompletedTask;};
}).AddScheme<AuthenticationSchemeOptions,RemoteSupportAuthenticationHandler>(RemoteSupportAuthenticationHandler.SchemeName,_=>{});
builder.Services.AddAuthorization(options=>options.AddPolicy(RemoteSupportAuthenticationHandler.PolicyName,
    policy=>policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme,RemoteSupportAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser().RequireRole("Admin","Support")));
await using(var app=builder.Build()) {
    app.UseAuthentication();app.UseAuthorization();
    app.MapGet("/operator",()=>Results.Ok()).RequireAuthorization(RemoteSupportAuthenticationHandler.PolicyName);
    app.MapGet("/test-login/{role}",async(HttpContext context,string role)=>{
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier,"user"),new Claim(ClaimTypes.Role,role)
        },CookieAuthenticationDefaults.AuthenticationScheme)));return Results.Ok();
    });
    await app.StartAsync();
    var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client=new HttpClient(new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false }) { BaseAddress=new Uri(url) };
    Check((await client.GetAsync("/operator")).StatusCode==HttpStatusCode.Unauthorized,"anonymous operator blocked");
    client.DefaultRequestHeaders.Add("X-NSX-Support-Key","wrong");
    Check((await client.GetAsync("/operator")).StatusCode==HttpStatusCode.Unauthorized,"wrong operator key blocked");
    client.DefaultRequestHeaders.Remove("X-NSX-Support-Key");client.DefaultRequestHeaders.Add("X-NSX-Support-Key",key);
    Check((await client.GetAsync("/operator")).IsSuccessStatusCode,"configured support key accepted");
    client.DefaultRequestHeaders.Remove("X-NSX-Support-Key");
    foreach(var role in new[]{"User","Admin"}) {
        using var login=await client.GetAsync("/test-login/"+role);
        client.DefaultRequestHeaders.Remove("Cookie");client.DefaultRequestHeaders.Add("Cookie",login.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        var result=await client.GetAsync("/operator");
        Check(role=="Admin" ? result.IsSuccessStatusCode : result.StatusCode==HttpStatusCode.Forbidden,"operator role "+role);
    }
    await app.StopAsync();
}
var principal=new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.NameIdentifier,"operator-a")},"test"));
Check(RemoteSupportAuthorization.OwnsRequest("operator-a",principal),"owner allowed");
Check(!RemoteSupportAuthorization.OwnsRequest("operator-b",principal),"other operator blocked");
var http=new DefaultHttpContext();http.Request.Host=new HostString("localhost:1234");http.Request.Scheme="http";
http.Request.Headers.Origin="http://localhost:1234";Check(RemoteSupportAuthorization.IsSameOrigin(http.Request),"same origin");
http.Request.Headers.Origin="https://attacker.example";Check(!RemoteSupportAuthorization.IsSameOrigin(http.Request),"cross origin blocked");
using(var json=JsonDocument.Parse("{\"status\":\"ok\",\"id\":\"receipt\"}"))
    Check(CaritakipExpoPushService.ParseResult(json.RootElement,true).ReceiptId=="receipt","Expo ticket id retained");
using(var json=JsonDocument.Parse("{\"status\":\"error\",\"details\":{\"error\":\"DeviceNotRegistered\"}}"))
    Check(CaritakipExpoPushService.ParseResult(json.RootElement,false).Error=="DeviceNotRegistered","Expo permanent error classified");
Console.WriteLine("PASS: anonymous/invalid key/role/operator ownership/origin controls, Expo ticket classification");

if(!args.Contains("--mysql")) return;
var secrets=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","nsx-web-local-development","secrets.json");
var config=new ConfigurationBuilder().AddJsonFile(secrets).Build();
var cs=new MySqlConnectionStringBuilder(config["ConnectionStrings:DefaultConnection"] ?? throw new Exception("Local test connection missing"));
if(cs.Server is not ("localhost" or "127.0.0.1" or "::1")) throw new Exception("Database integration test accepts localhost only");
// Never use the configured application database. Only this fresh generated schema is touched.
var testDb="nsx_release_test_"+Guid.NewGuid().ToString("N");cs.Database="";cs.GuidFormat=MySqlGuidFormat.None;
await using var admin=new MySqlConnection(cs.ConnectionString);await admin.OpenAsync();
await new MySqlCommand($"CREATE DATABASE `{testDb}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;",admin).ExecuteNonQueryAsync();
try {
    cs.Database=testDb;
    var testConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:DefaultConnection"]=cs.ConnectionString }).Build();
    var cloud=new CaritakipCloudStore(testConfig,NullLogger<CaritakipCloudStore>.Instance,null!);
    var native=new CaritakipNativeMobileStore(testConfig,NullLogger<CaritakipNativeMobileStore>.Instance,null!,cloud);
    var queue=new CaritakipNotificationOutboxStore(testConfig,cloud);
    await native.EnsureSchemaAsync();
    await using var db=new MySqlConnection(cs.ConnectionString);await db.OpenAsync();
    var tenant=Guid.NewGuid().ToString();var first=Guid.NewGuid().ToString();var second=Guid.NewGuid().ToString();
    async Task Execute(string sql) { await new MySqlCommand(sql,db).ExecuteNonQueryAsync(); }
    async Task<long> Count(string table) => Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM "+table,db).ExecuteScalarAsync());
    await Execute($"INSERT INTO ct_tenants(tenant_id,license_id,company_name,created_at_utc,updated_at_utc) VALUES('{tenant}',1,'Test',UTC_TIMESTAMP(),UTC_TIMESTAMP());");
    foreach(var device in new[]{first,second})
        await Execute($"INSERT INTO ct_mobile_native_devices(tenant_id,mobile_device_id,created_at_utc,updated_at_utc,push_expo_token) VALUES('{tenant}','{device}',UTC_TIMESTAMP(),UTC_TIMESTAMP(),'ExponentPushToken[{device}]');");
    var debt=Guid.NewGuid().ToString();
    CariMutationRequest Mutation(string id,string operation="upsert") => new() { ClientMutationId=id,EntityType="debt",EntityId=id,Operation=operation,
        Payload=JsonSerializer.SerializeToElement(new{customerId=Guid.NewGuid().ToString(),amount=100.50,description="test"}) };
    var rollback=Guid.NewGuid().ToString();
    try { await cloud.ApplyMutationsAsync(tenant,"desktop",new[]{Mutation(rollback),Mutation(Guid.NewGuid().ToString(),"invalid")},default);throw new Exception("rollback expected"); }
    catch(ArgumentException) {}
    Check(await Count("ct_entities")==0 && await Count("ct_notification_outbox")==0,"mutation and outbox rollback together");
    var mutation=Mutation(debt);await cloud.ApplyMutationsAsync(tenant,"desktop",new[]{mutation},default);
    await cloud.ApplyMutationsAsync(tenant,"desktop",new[]{mutation},default);
    Check(await Count("ct_notification_outbox")==1,"committed mutation creates one durable notification");
    var claims=await Task.WhenAll(queue.ClaimAsync(default),queue.ClaimAsync(default));
    Check(claims.Count(job=>job is not null)==1,"two workers cannot claim same job");
    var job=claims.First(job=>job is not null)!;
    // Fail the second device write, then retry the same stable message id.
    await Execute($"CREATE TRIGGER fail_delivery BEFORE INSERT ON ct_mobile_inbox FOR EACH ROW BEGIN IF NEW.mobile_device_id='{second}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected failure'; END IF; END;");
    var factory = new FakeFactory();
    var expo=new CaritakipExpoPushService(factory,testConfig,NullLogger<CaritakipExpoPushService>.Instance);
    var deliverer=new CaritakipMobileNotificationDeliverer(native,queue,new FakeHub(),NullLogger<CaritakipMobileNotificationDeliverer>.Instance);
    var worker=new CaritakipNotificationOutboxWorker(queue,deliverer,expo,native,NullLogger<CaritakipNotificationOutboxWorker>.Instance);
    await worker.ProcessAsync(job,default);
    Check(Convert.ToInt64(await new MySqlCommand($"SELECT state FROM ct_notification_outbox WHERE id='{job.Id}'",db).ExecuteScalarAsync())==0,"partial delivery stays pending");
    await Execute("DROP TRIGGER fail_delivery;");
    // Seed an already delivered/deleted first-device row to prove retries don't resurrect it.
    await native.InsertInboxMessageAsync(tenant,first,job.Id,"debts","test","test",null,default);
    await Execute($"DELETE FROM ct_mobile_inbox WHERE mobile_device_id='{first}'; UPDATE ct_notification_outbox SET available_at_utc=UTC_TIMESTAMP(6) WHERE id='{job.Id}';");
    var retry=(await queue.ClaimAsync(default))!;await worker.ProcessAsync(retry,default);
    Check(await Count("ct_mobile_inbox")==1,"retry preserves deleted first inbox and creates missing second inbox");
    Check(await Count("ct_notification_outbox")==3,"one push job per device despite retry");
    var pushJob=(await queue.ClaimAsync(default))!;await worker.ProcessAsync(pushJob,default);
    if (JsonSerializer.Deserialize<PushDraft>(pushJob.Payload)!.DeviceId == first) {
        Check(factory.SendRequests == 0,"deleted inbox push is suppressed");
        pushJob=(await queue.ClaimAsync(default))!;await worker.ProcessAsync(pushJob,default);
    }
    Check((string)(await new MySqlCommand($"SELECT kind FROM ct_notification_outbox WHERE id='{pushJob.Id}'",db).ExecuteScalarAsync())! == "receipt","accepted push waits durably for receipt");
    var payload=(string)(await new MySqlCommand($"SELECT payload_json FROM ct_notification_outbox WHERE id='{pushJob.Id}'",db).ExecuteScalarAsync())!;
    Check(JsonSerializer.Deserialize<PushDraft>(payload)!.ReceiptId=="test-receipt","receipt persisted");
    await Execute($"UPDATE ct_notification_outbox SET available_at_utc=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY) WHERE id<>'{pushJob.Id}' AND state=0; UPDATE ct_notification_outbox SET available_at_utc=UTC_TIMESTAMP(6) WHERE id='{pushJob.Id}';");
    factory.Status=HttpStatusCode.ServiceUnavailable;
    var receipt=(await queue.ClaimAsync(default,"push"))!;await worker.ProcessAsync(receipt,default);
    Check(factory.SendRequests==1,"receipt transport failure does not resend accepted push");
    Check((string)(await new MySqlCommand($"SELECT kind FROM ct_notification_outbox WHERE id='{receipt.Id}'",db).ExecuteScalarAsync())! == "receipt","receipt remains pending after HTTP 503");
    factory.Status=HttpStatusCode.OK;
    factory.ReceiptBody="{\"data\":{\"test-receipt\":{\"status\":\"ok\"}}}";
    await Execute($"UPDATE ct_notification_outbox SET available_at_utc=UTC_TIMESTAMP(6) WHERE id='{receipt.Id}';");
    await worker.ProcessAsync((await queue.ClaimAsync(default,"push"))!,default);
    Check(Convert.ToInt64(await new MySqlCommand($"SELECT state FROM ct_notification_outbox WHERE id='{receipt.Id}'",db).ExecuteScalarAsync())==1,"successful receipt completes durable push");
    var invalidId=Guid.NewGuid().ToString();
    await native.InsertInboxMessageAsync(tenant,second,invalidId,"debts","test","test",null,default);
    var invalid=new PushDraft(tenant,second,new CaritakipExpoPushMessage { To=$"ExponentPushToken[{second}]",Title="test",Body="test",Data=new() { ["messageId"]=invalidId,["category"]="debts" } });
    await queue.EnqueueAsync(tenant,"invalid-token-test","push",invalid,default);
    factory.SendBody="{\"data\":[{\"status\":\"error\",\"details\":{\"error\":\"DeviceNotRegistered\"}}]}";
    await worker.ProcessAsync((await queue.ClaimAsync(default,"push"))!,default);
    Check(await new MySqlCommand($"SELECT push_expo_token FROM ct_mobile_native_devices WHERE mobile_device_id='{second}'",db).ExecuteScalarAsync() is DBNull,"invalid token is removed");
    var cutoff=DateTime.UtcNow;
    var older=Guid.NewGuid().ToString();var newer=Guid.NewGuid().ToString();
    await native.InsertInboxMessageAsync(tenant,second,older,"system","older","test",null,default);
    await native.InsertInboxMessageAsync(tenant,second,newer,"system","newer","test",null,default);
    await Execute($"UPDATE ct_mobile_inbox SET created_at_utc=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 1 DAY) WHERE message_id='{older}'; UPDATE ct_mobile_inbox SET created_at_utc=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 DAY) WHERE message_id='{newer}';");
    await native.DeleteInboxAsync(new CariNativeAccessContext(tenant,"Test",1,second,"test",DateTime.UtcNow.AddHours(1)),null,true,default,cutoff);
    Check(Convert.ToInt64(await new MySqlCommand($"SELECT COUNT(*) FROM ct_mobile_inbox WHERE message_id='{older}'",db).ExecuteScalarAsync())==0,"bulk clear deletes old history");
    Check(Convert.ToInt64(await new MySqlCommand($"SELECT COUNT(*) FROM ct_mobile_inbox WHERE message_id='{newer}'",db).ExecuteScalarAsync())==1,"bulk clear preserves newer notification");
    Console.WriteLine("PASS: isolated MySQL schema, atomic mutation rollback, dedupe, concurrent leases, partial delivery recovery, deleted inbox protection, durable push receipt");
} finally {
    // Name is generated above and guarded; never delete an application schema.
    if(!testDb.StartsWith("nsx_release_test_") || testDb.Length!=49) throw new Exception("Invalid test schema name");
    await new MySqlCommand($"DROP DATABASE `{testDb}`;",admin).ExecuteNonQueryAsync();
}

sealed class FakeFactory : IHttpClientFactory {
    public HttpStatusCode Status {get;set;}=HttpStatusCode.OK;
    public string SendBody {get;set;}="{\"data\":[{\"status\":\"ok\",\"id\":\"test-receipt\"}]}";
    public string ReceiptBody {get;set;}="{\"data\":{}}";
    public int SendRequests {get;set;}
    public HttpClient CreateClient(string name)=>new(new FakeHandler(this));
}
sealed class FakeHandler(FakeFactory factory) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        var receipt=request.RequestUri!.AbsolutePath.EndsWith("getReceipts");
        if(!receipt) factory.SendRequests++;
        return Task.FromResult(new HttpResponseMessage(factory.Status) { Content=new StringContent(receipt ? factory.ReceiptBody : factory.SendBody) });
    }
}
sealed class FakeProxy : IClientProxy { public Task SendCoreAsync(string method,object?[] args,CancellationToken ct=default)=>Task.CompletedTask; }
sealed class FakeHub : IHubContext<CaritakipCloudHub> {
    public IHubClients Clients {get;}=new FakeClients();public IGroupManager Groups=>throw new NotImplementedException();
}
sealed class FakeClients : IHubClients {
    private readonly IClientProxy proxy=new FakeProxy();public IClientProxy All=>proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> ids)=>proxy;public IClientProxy Client(string id)=>proxy;
    public IClientProxy Clients(IReadOnlyList<string> ids)=>proxy;public IClientProxy Group(string group)=>proxy;
    public IClientProxy GroupExcept(string group,IReadOnlyList<string> ids)=>proxy;public IClientProxy Groups(IReadOnlyList<string> groups)=>proxy;
    public IClientProxy User(string id)=>proxy;public IClientProxy Users(IReadOnlyList<string> ids)=>proxy;
}
