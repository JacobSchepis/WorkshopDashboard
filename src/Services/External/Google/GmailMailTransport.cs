using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MimeKit;
using WorkshopDashboard.Services.External.Email;

namespace WorkshopDashboard.Services.External.Google;

/// <summary>Sends mail through the Gmail API as the authorised account (send scope only, its own
/// token folder — see GoogleAuthorizer).</summary>
public class GmailMailTransport : IMailTransport
{
    private static readonly string[] Scopes = { GmailService.Scope.GmailSend };
    private const string ApplicationName = "WorkshopDashboard";

    private readonly GoogleAuthorizer _auth;
    private GmailService? _gmailService;

    public GmailMailTransport(GoogleAuthorizer auth) => _auth = auth;

    public async Task SendAsync(MimeMessage message)
    {
        var service = await GetServiceAsync();

        using var ms = new MemoryStream();
        await message.WriteToAsync(ms);
        var raw = Convert.ToBase64String(ms.ToArray())
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        await service.Users.Messages.Send(new Message { Raw = raw }, "me").ExecuteAsync();
    }

    private async Task<GmailService> GetServiceAsync()
    {
        if (_gmailService != null)
            return _gmailService;

        var credential = await _auth.AuthorizeAsync(Scopes, "token-store");
        _gmailService = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
        return _gmailService;
    }
}
