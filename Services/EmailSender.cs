using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SsrsReportScheduler.Models;
using SsrsReportScheduler.Options;

namespace SsrsReportScheduler.Services;

public sealed class EmailSender
{
    private readonly IOptions<EmailOptions> _options;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IOptions<EmailOptions> options, ILogger<EmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task SendReportAsync(ReportTaskOptions task, ReportRenderingResult result, string attachmentFileName, CancellationToken cancellationToken)
    {
        var delivery = task.Delivery.Email;
        if (delivery.To.Count == 0 && delivery.Cc.Count == 0 && delivery.Bcc.Count == 0)
        {
            throw new InvalidOperationException($"Task '{task.Name}' is configured for email delivery but has no recipients.");
        }

        var emailOptions = _options.Value;
        if (string.IsNullOrWhiteSpace(emailOptions.Host))
        {
            throw new InvalidOperationException("Email host is not configured.");
        }

        if (string.IsNullOrWhiteSpace(emailOptions.FromAddress))
        {
            throw new InvalidOperationException("Email FromAddress is not configured.");
        }

        using var message = BuildMailMessage(task, result, attachmentFileName, emailOptions);

        using var client = new SmtpClient(emailOptions.Host, emailOptions.Port)
        {
            EnableSsl = emailOptions.UseSsl
        };

        if (!string.IsNullOrWhiteSpace(emailOptions.Username))
        {
            client.Credentials = new NetworkCredential(emailOptions.Username, emailOptions.Password);
        }

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Sending report '{ReportName}' via email to {Recipients}.", task.Name, string.Join(", ", message.To.Select(r => r.Address)));

        await client.SendMailAsync(message).ConfigureAwait(false);
    }

    private static MailMessage BuildMailMessage(ReportTaskOptions task, ReportRenderingResult result, string attachmentFileName, EmailOptions options)
    {
        var delivery = task.Delivery.Email;

        var message = new MailMessage
        {
            Subject = string.IsNullOrWhiteSpace(delivery.Subject) ? $"{task.Name} report" : delivery.Subject,
            Body = string.IsNullOrWhiteSpace(delivery.Body) ? "Report attached." : delivery.Body,
            IsBodyHtml = delivery.IsBodyHtml
        };

        message.From = string.IsNullOrWhiteSpace(options.FromDisplayName)
            ? new MailAddress(options.FromAddress)
            : new MailAddress(options.FromAddress, options.FromDisplayName);

        foreach (var address in delivery.To)
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                message.To.Add(address);
            }
        }

        foreach (var address in delivery.Cc)
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                message.CC.Add(address);
            }
        }

        foreach (var address in delivery.Bcc)
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                message.Bcc.Add(address);
            }
        }

        var contentStream = new MemoryStream(result.Content, writable: false);
        var attachment = new Attachment(contentStream, attachmentFileName, result.MimeType);
        message.Attachments.Add(attachment);

        return message;
    }
}
