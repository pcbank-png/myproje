using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NSYazilim.Web.Services;

// Browser operators use the existing Admin cookie. Desktop operators can use
// an individually configured machine key; no anonymous or query-string keys.
public sealed class RemoteSupportAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "RemoteSupportKey";
    public const string PolicyName = "RemoteSupportOperator";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var supplied = Request.Headers["X-NSX-Support-Key"].ToString();
        if (string.IsNullOrEmpty(supplied)) return Task.FromResult(AuthenticateResult.NoResult());
        if (supplied.Length > 512) return Task.FromResult(AuthenticateResult.Fail("Invalid support key"));
        foreach (var entry in configuration.GetSection("RemoteSupport:Operators").GetChildren())
        {
            var secret = entry["Key"];
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32) continue;
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(supplied)))) continue;
            var identity = new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.NameIdentifier, "support:" + entry.Key),
                new Claim(ClaimTypes.Name, entry["Name"] ?? entry.Key),
                new Claim(ClaimTypes.Role, "Support")
            }, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
        return Task.FromResult(AuthenticateResult.Fail("Invalid support key"));
    }
}
