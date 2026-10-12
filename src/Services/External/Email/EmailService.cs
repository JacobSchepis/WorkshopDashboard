using WorkshopDashboard.Models;
using MimeKit;

namespace WorkshopDashboard.Services.External.Email;

public class EmailAttachment
{
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();

    public static EmailAttachment FromFile(string path) => new()
    {
        FileName = Path.GetFileName(path),
        Content = File.ReadAllBytes(path)
    };
}

public class EmailBuilder
{
    private readonly EmailService _service;

    private readonly List<string> _to = new();
    private readonly List<string> _cc = new();
    private readonly List<string> _bcc = new();
    private readonly List<EmailAttachment> _attachments = new();
    private string _subject = string.Empty;
    private string _bodyHtml = string.Empty;
    private string? _bodyText;
    private string _fromName = CompanyProfile.Name;
    private string _fromAddress = "me";

    internal EmailBuilder(EmailService service)
    {
        _service = service;
    }

    public EmailBuilder To(params string[] addresses)
    {
        _to.AddRange(addresses);
        return this;
    }

    public EmailBuilder Cc(params string[] addresses)
    {
        _cc.AddRange(addresses);
        return this;
    }

    public EmailBuilder Bcc(params string[] addresses)
    {
        _bcc.AddRange(addresses);
        return this;
    }

    public EmailBuilder Subject(string subject)
    {
        _subject = subject;
        return this;
    }

    public EmailBuilder HtmlBody(string html)
    {
        _bodyHtml = html;
        return this;
    }

    public EmailBuilder TextBody(string text)
    {
        _bodyText = text;
        return this;
    }

    /// <summary>
    /// Set the sender display name, and optionally the send-from address.
    /// The address must be a verified "Send mail as" alias on the authorized
    /// Gmail account (e.g. a group address like workshop@example.com), otherwise
    /// Gmail silently rewrites it back to the account's own address.
    /// </summary>
    public EmailBuilder From(string displayName, string? address = null)
    {
        _fromName = displayName;
        if (address != null)
            _fromAddress = address;
        return this;
    }

    /// <summary>Set only the send-from address (must be a verified alias).</summary>
    public EmailBuilder FromAddress(string address)
    {
        _fromAddress = address;
        return this;
    }

    public EmailBuilder Attach(EmailAttachment attachment)
    {
        _attachments.Add(attachment);
        return this;
    }

    public EmailBuilder Attach(string fileName, byte[] content)
    {
        _attachments.Add(new EmailAttachment { FileName = fileName, Content = content });
        return this;
    }

    public EmailBuilder AttachFile(string path)
    {
        _attachments.Add(EmailAttachment.FromFile(path));
        return this;
    }

    public EmailBuilder AttachRange(IEnumerable<EmailAttachment> attachments)
    {
        _attachments.AddRange(attachments);
        return this;
    }

    public async Task SendAsync()
    {
        if (!_to.Any())
            throw new InvalidOperationException("Email must have at least one recipient. Call .To() before .SendAsync().");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_fromName, _fromAddress));
        foreach (var addr in _to)
            mime.To.Add(MailboxAddress.Parse(addr));
        foreach (var addr in _cc)
            mime.Cc.Add(MailboxAddress.Parse(addr));
        foreach (var addr in _bcc)
            mime.Bcc.Add(MailboxAddress.Parse(addr));
        mime.Subject = _subject;

        var builder = new BodyBuilder { HtmlBody = _bodyHtml };
        if (_bodyText != null)
            builder.TextBody = _bodyText;
        foreach (var att in _attachments)
            builder.Attachments.Add(att.FileName, att.Content);
        mime.Body = builder.ToMessageBody();

        await _service.SendMimeAsync(mime);
    }
}

/// <summary>Delivers a finished message — Gmail normally (GmailMailTransport), an in-memory
/// outbox in demo mode (DemoMailOutbox).</summary>
public interface IMailTransport
{
    Task SendAsync(MimeMessage message);
}

/// <summary>Composes outgoing email (see EmailBuilder) and hands it to the configured transport.</summary>
public class EmailService
{
    private readonly IMailTransport _transport;

    public EmailService(IMailTransport transport) => _transport = transport;

    /// <summary>Start composing a new email.</summary>
    public EmailBuilder Compose() => new(this);

    internal Task SendMimeAsync(MimeMessage mime) => _transport.SendAsync(mime);
}
