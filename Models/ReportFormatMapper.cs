using System;

namespace SsrsReportScheduler.Models;

public static class ReportFormatMapper
{
    public static ReportRenderFormat Parse(string value)
    {
        if (string.Equals(value, "PDF", StringComparison.OrdinalIgnoreCase))
        {
            return ReportRenderFormat.Pdf;
        }

        if (string.Equals(value, "EXCEL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "XLS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "XLSX", StringComparison.OrdinalIgnoreCase))
        {
            return ReportRenderFormat.Excel;
        }

        throw new ArgumentException($"Unsupported report format '{value}'.", nameof(value));
    }

    public static string ToSsrsFormat(ReportRenderFormat format) => format switch
    {
        ReportRenderFormat.Pdf => "PDF",
        ReportRenderFormat.Excel => "EXCELOPENXML",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported SSRS format.")
    };

    public static string GetFileExtension(ReportRenderFormat format) => format switch
    {
        ReportRenderFormat.Pdf => ".pdf",
        ReportRenderFormat.Excel => ".xlsx",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported file extension.")
    };

    public static string GetMimeType(ReportRenderFormat format) => format switch
    {
        ReportRenderFormat.Pdf => "application/pdf",
        ReportRenderFormat.Excel => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported mime type.")
    };
}
