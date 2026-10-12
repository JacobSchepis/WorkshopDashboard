using System.Collections.Concurrent;
using MimeKit;
using WorkshopDashboard.Services.External.Email;

namespace WorkshopDashboard.Demo;

/// <summary>Demo mode's mail transport: nothing leaves the app. Each message is logged and kept
/// (the most recent 50) so it could be shown, and the page that sent it reports success as usual.</summary>
public sealed class DemoMailOutbox(ILogger<DemoMailOutbox> logger) : IMailTransport
{
    private const int Keep = 50;
    private readonly ConcurrentQueue<MimeMessage> _sent = new();

    public IReadOnlyList<MimeMessage> Sent => _sent.ToList();

    public Task SendAsync(MimeMessage message)
    {
        _sent.Enqueue(message);
        while (_sent.Count > Keep) _sent.TryDequeue(out _);
        logger.LogInformation("Not sent (demo): \"{Subject}\" to {To}", message.Subject, string.Join(", ", message.To));
        return Task.CompletedTask;
    }
}
