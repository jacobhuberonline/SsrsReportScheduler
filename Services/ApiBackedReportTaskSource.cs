using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SsrsReportScheduler.Options;

namespace SsrsReportScheduler.Services;

public sealed class ApiBackedReportTaskSource : IReportTaskSource
{
    private readonly SystemOptionsApiClient _apiClient;
    private readonly IOptionsMonitor<SchedulerOptions> _schedulerOptions;
    private readonly ILogger<ApiBackedReportTaskSource> _logger;

    public ApiBackedReportTaskSource(
        SystemOptionsApiClient apiClient,
        IOptionsMonitor<SchedulerOptions> schedulerOptions,
        ILogger<ApiBackedReportTaskSource> logger)
    {
        _apiClient = apiClient;
        _schedulerOptions = schedulerOptions;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ReportTaskOptions>> GetReportTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = await _apiClient.TryGetReportTasksAsync(cancellationToken).ConfigureAwait(false);
        if (tasks.Count > 0)
        {
            _logger.LogInformation("Loaded {TaskCount} report tasks from system options API.", tasks.Count);
            return tasks;
        }

        var fallback = _schedulerOptions.CurrentValue.ReportTasks;
        if (fallback is null || fallback.Count == 0)
        {
            _logger.LogWarning("No report tasks were found in configuration or via the system options API.");
            return Array.Empty<ReportTaskOptions>();
        }

        _logger.LogInformation("Falling back to {TaskCount} report tasks from configuration.", fallback.Count);
        return fallback.Select(CloneTask).ToArray();
    }

    private static ReportTaskOptions CloneTask(ReportTaskOptions source)
    {
        var email = source.Delivery.Email ?? new Models.EmailDeliveryOptions();
        var sftp = source.Delivery.Sftp ?? new Models.SftpDeliveryOptions();

        var clone = new ReportTaskOptions
        {
            Name = source.Name,
            Format = source.Format,
            CronExpression = source.CronExpression,
            Parameters = source.Parameters is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(source.Parameters, StringComparer.OrdinalIgnoreCase),
            Delivery = new Models.ReportDeliveryOptions
            {
                Method = source.Delivery.Method,
                Email = new Models.EmailDeliveryOptions
                {
                    To = new List<string>(email.To),
                    Cc = new List<string>(email.Cc),
                    Bcc = new List<string>(email.Bcc),
                    Subject = email.Subject,
                    Body = email.Body,
                    IsBodyHtml = email.IsBodyHtml,
                    AttachmentFileName = email.AttachmentFileName
                },
                Sftp = new Models.SftpDeliveryOptions
                {
                    RemoteDirectory = sftp.RemoteDirectory,
                    FileName = sftp.FileName
                }
            }
        };

        return clone;
    }
}
