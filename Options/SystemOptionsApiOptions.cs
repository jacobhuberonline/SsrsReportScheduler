namespace SsrsReportScheduler.Options;

public sealed class SystemOptionsApiOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string SystemOptionsPath { get; set; } = "/api/system/options";
    public string ReportTasksPath { get; set; } = "/api/system/options/report-tasks";
    public string? ApiKey { get; set; }
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
    public int TimeoutSeconds { get; set; } = 30;
}
