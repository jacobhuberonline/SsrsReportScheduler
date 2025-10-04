using System.Collections.Generic;

namespace SsrsReportScheduler.Options;

public sealed class ReportTaskOptions
{
    public string Name { get; set; } = string.Empty;
    public string ReportPath { get; set; } = string.Empty;
    public string Format { get; set; } = "PDF";
    public string CronExpression { get; set; } = "0 0 6 * * ?";
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string RemoteDirectory { get; set; } = string.Empty;
}
