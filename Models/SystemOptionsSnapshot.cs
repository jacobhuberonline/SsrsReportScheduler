using SsrsReportScheduler.Options;

namespace SsrsReportScheduler.Models;

public sealed record SystemOptionsSnapshot(IReadOnlyList<ReportTaskOptions> ReportTasks, DateTimeOffset RetrievedAt);
