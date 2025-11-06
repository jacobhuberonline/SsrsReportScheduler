namespace SsrsReportScheduler.Options;

public sealed class ReportServerOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string ExecutionEndpoint { get; set; } = "/ReportExecution2005.asmx";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;
}
