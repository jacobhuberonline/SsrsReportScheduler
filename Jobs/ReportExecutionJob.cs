using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using SsrsReportScheduler.Models;
using SsrsReportScheduler.Options;
using SsrsReportScheduler.Services;

namespace SsrsReportScheduler.Jobs;

public sealed class ReportExecutionJob : IJob
{
    public const string TaskNameKey = "TaskName";

    private readonly SsrsReportClient _ssrsClient;
    private readonly SftpUploader _sftpUploader;
    private readonly EmailSender _emailSender;
    private readonly IOptions<OutputOptions> _outputOptions;
    private readonly IOptionsMonitor<SchedulerOptions> _schedulerOptions;
    private readonly IReportTaskSource _reportTaskSource;
    private readonly ILogger<ReportExecutionJob> _logger;

    public ReportExecutionJob(
        SsrsReportClient ssrsClient,
        SftpUploader sftpUploader,
        EmailSender emailSender,
        IOptions<OutputOptions> outputOptions,
        IOptionsMonitor<SchedulerOptions> schedulerOptions,
        IReportTaskSource reportTaskSource,
        ILogger<ReportExecutionJob> logger)
    {
        _ssrsClient = ssrsClient;
        _sftpUploader = sftpUploader;
        _emailSender = emailSender;
        _outputOptions = outputOptions;
        _schedulerOptions = schedulerOptions;
        _reportTaskSource = reportTaskSource;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var taskName = context.JobDetail.JobDataMap.GetString(TaskNameKey);

        if (string.IsNullOrWhiteSpace(taskName))
        {
            _logger.LogError("ReportExecutionJob missing task name.");
            return;
        }

        ReportTaskOptions? taskConfig = null;

        try
        {
            var tasks = await _reportTaskSource.GetReportTasksAsync(cancellationToken).ConfigureAwait(false);
            taskConfig = tasks.FirstOrDefault(t => string.Equals(t.Name, taskName, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve task '{TaskName}' from report task source. Falling back to configuration.", taskName);
        }

        taskConfig ??= _schedulerOptions
            .CurrentValue
            .ReportTasks
            .FirstOrDefault(t => string.Equals(t.Name, taskName, StringComparison.OrdinalIgnoreCase));

        if (taskConfig is null)
        {
            _logger.LogError("Report task '{TaskName}' was not found in configuration.", taskName);
            return;
        }

        try
        {
            _logger.LogInformation("Starting report execution for '{ReportName}'.", taskConfig.Name);

            var renderResult = await _ssrsClient.ExecuteAsync(taskConfig, cancellationToken).ConfigureAwait(false);

            var fileName = BuildDefaultFileName(taskConfig.Name, renderResult.FileExtension);

            switch (taskConfig.Delivery.Method)
            {
                case ReportDeliveryMethod.Email:
                    await HandleEmailDeliveryAsync(taskConfig, renderResult, fileName, cancellationToken).ConfigureAwait(false);
                    break;

                case ReportDeliveryMethod.Sftp:
                case ReportDeliveryMethod.Unknown:
                default:
                    await HandleSftpDeliveryAsync(taskConfig, renderResult, fileName, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute report '{ReportName}'.", taskConfig.Name);
            throw;
        }
    }

    private async Task HandleSftpDeliveryAsync(ReportTaskOptions task, ReportRenderingResult result, string defaultFileName, CancellationToken cancellationToken)
    {
        var fileName = ResolveFileName(task.Delivery.Sftp.FileName, defaultFileName, result.FileExtension);
        var localPath = await SaveToLocalFileAsync(result.Content, fileName, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Report '{ReportName}' saved to {LocalPath}.", task.Name, localPath);

        var remotePath = await _sftpUploader.UploadAsync(
            result.Content,
            fileName,
            cancellationToken,
            task.Delivery.Sftp.RemoteDirectory).ConfigureAwait(false);

        _logger.LogInformation("Report '{ReportName}' uploaded to {RemotePath}.", task.Name, remotePath);
    }

    private async Task HandleEmailDeliveryAsync(ReportTaskOptions task, ReportRenderingResult result, string defaultFileName, CancellationToken cancellationToken)
    {
        var attachmentFileName = ResolveFileName(task.Delivery.Email.AttachmentFileName, defaultFileName, result.FileExtension);
        await _emailSender.SendReportAsync(task, result, attachmentFileName, cancellationToken).ConfigureAwait(false);
        var recipients = task.Delivery.Email.To.Concat(task.Delivery.Email.Cc).Concat(task.Delivery.Email.Bcc).ToArray();
        _logger.LogInformation("Report '{ReportName}' emailed to {Recipients}.", task.Name, string.Join(", ", recipients));
    }

    private async Task<string> SaveToLocalFileAsync(byte[] content, string fileName, CancellationToken cancellationToken)
    {
        var baseDirectory = _outputOptions.Value.Directory;
        var absoluteDirectory = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(absoluteDirectory);

        var targetPath = Path.Combine(absoluteDirectory, fileName);

        await File.WriteAllBytesAsync(targetPath, content, cancellationToken).ConfigureAwait(false);
        return targetPath;
    }

    private static string BuildDefaultFileName(string taskName, string fileExtension)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safeName = SanitizeFileName(taskName);
        var extension = string.IsNullOrWhiteSpace(fileExtension) ? string.Empty : fileExtension;
        return $"{safeName}_{timestamp}{extension}";
    }

    private static string ResolveFileName(string? requested, string fallback, string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return fallback;
        }

        var sanitized = SanitizeFileName(requested);
        if (!Path.HasExtension(sanitized) && !string.IsNullOrWhiteSpace(fileExtension))
        {
            sanitized += fileExtension;
        }

        return sanitized;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "report";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new char[name.Length];
        var index = 0;
        foreach (var c in name.Trim())
        {
            builder[index++] = invalid.Contains(c) ? '_' : c;
        }

        return new string(builder, 0, index);
    }
}
