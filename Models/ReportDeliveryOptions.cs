namespace SsrsReportScheduler.Models;

public enum ReportDeliveryMethod
{
    Unknown = 0,
    Sftp = 1,
    Email = 2
}

public sealed class ReportDeliveryOptions
{
    public ReportDeliveryMethod Method { get; set; } = ReportDeliveryMethod.Sftp;
    public EmailDeliveryOptions Email { get; set; } = new();
    public SftpDeliveryOptions Sftp { get; set; } = new();
}

public sealed class EmailDeliveryOptions
{
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsBodyHtml { get; set; }
    public string? AttachmentFileName { get; set; }
}

public sealed class SftpDeliveryOptions
{
    public string? RemoteDirectory { get; set; }
    public string? FileName { get; set; }
}
