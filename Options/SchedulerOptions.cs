using System.Collections.Generic;

namespace SsrsReportScheduler.Options;

public sealed class SchedulerOptions
{
    public List<ReportTaskOptions> ReportTasks { get; set; } = new();
}
