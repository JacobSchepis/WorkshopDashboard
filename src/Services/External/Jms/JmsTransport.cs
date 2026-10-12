using System.Net;

namespace WorkshopDashboard.Services.External.Jms;

/// <summary>What carries JmsClient's requests. Normally the network; in demo mode an in-process
/// handler that answers like JMS does (see Demo/DemoJmsHandler), so every other part of the
/// JMS layer — logins, session checks, form posts, response parsing — runs unchanged.
/// The handler is given each client's cookie jar so it can hand out and check session cookies
/// the way JMS's own login does.</summary>
public sealed class JmsTransport
{
    public static readonly JmsTransport Network = new(null);

    private readonly Func<CookieContainer, HttpMessageHandler>? _createHandler;

    public JmsTransport(Func<CookieContainer, HttpMessageHandler>? createHandler) => _createHandler = createHandler;

    internal HttpMessageHandler CreateHandler(CookieContainer cookieJar) =>
        _createHandler?.Invoke(cookieJar) ?? new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = cookieJar,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 10
        };
}
