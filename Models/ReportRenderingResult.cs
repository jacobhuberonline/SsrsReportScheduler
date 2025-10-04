namespace SsrsReportScheduler.Models;

public sealed record ReportRenderingResult(
    byte[] Content,
    ReportRenderFormat Format,
    string FileExtension,
    string MimeType);
