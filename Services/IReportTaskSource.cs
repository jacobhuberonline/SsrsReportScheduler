using SsrsReportScheduler.Options;

namespace SsrsReportScheduler.Services;

public interface IReportTaskSource
{
    Task<IReadOnlyList<ReportTaskOptions>> GetReportTasksAsync(CancellationToken cancellationToken);
}
