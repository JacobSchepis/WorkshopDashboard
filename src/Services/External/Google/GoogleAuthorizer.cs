using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Options;

namespace WorkshopDashboard.Services.External.Google;

// One OAuth client (GoogleOptions) shared by Gmail and both Sheets services. Each caller passes
// its OWN token folder: FileDataStore caches the token under the "user" key alone, with no
// regard to which scopes it was granted, so a shared folder would hand the Sheets services a
// Gmail-only token that the Sheets API then rejects at request time (403 "insufficient
// authentication scopes"). A separate folder per scope set forces its own consent instead.
public class GoogleAuthorizer
{
    private readonly GoogleOptions _options;
    private readonly string _contentRoot;

    public GoogleAuthorizer(IOptions<GoogleOptions> options, IWebHostEnvironment env)
    {
        _options = options.Value;
        _contentRoot = env.ContentRootPath;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    /// <summary>The first call for a token folder opens a browser for one-time consent
    /// (loopback OAuth flow) — someone has to click through it; it can't run headlessly.</summary>
    public Task<UserCredential> AuthorizeAsync(string[] scopes, string tokenFolder, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Google isn't configured — set Google:ClientId and Google:ClientSecret.");

        var secrets = new ClientSecrets { ClientId = _options.ClientId, ClientSecret = _options.ClientSecret };
        var tokenPath = Path.Combine(_contentRoot, _options.TokenStorePath, tokenFolder);
        return GoogleWebAuthorizationBroker.AuthorizeAsync(secrets, scopes, "user", ct, new FileDataStore(tokenPath, true));
    }
}
