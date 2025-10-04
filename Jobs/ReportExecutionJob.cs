using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using SsrsReportScheduler.Models;
using SsrsReportScheduler.Options;
using SsrsReportScheduler.Services;

namespace SsrsReportScheduler.Jobs;

/// <summary>
/// Quartz job that executes a single SSRS report run and handles persistence + upload.
/// </summary>
public sealed class ReportExecutionJob : IJob
{
    public const string TaskNameKey = "TaskName";

    private readonly SsrsReportClient _ssrsClient;
    private readonly SftpUploader _sftpUploader;
    private readonly IOptions<OutputOptions> _outputOptions;
    private readonly IOptionsMonitor<SchedulerOptions> _schedulerOptions;
    private readonly ILogger<ReportExecutionJob> _logger;

    public ReportExecutionJob(
        SsrsReportClient ssrsClient,
        SftpUploader sftpUploader,
        IOptions<OutputOptions> outputOptions,
        IOptionsMonitor<SchedulerOptions> schedulerOptions,
        ILogger<ReportExecutionJob> logger)
    {
        _ssrsClient = ssrsClient;
        _sftpUploader = sftpUploader;
        _outputOptions = outputOptions;
        _schedulerOptions = schedulerOptions;
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

        var taskConfig = _schedulerOptions
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

            var localPath = await SaveToLocalFileAsync(taskConfig, renderResult, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Report '{ReportName}' saved to {LocalPath}.", taskConfig.Name, localPath);

            var remotePath = await _sftpUploader.UploadAsync(
                renderResult.Content,
                taskConfig.RemoteDirectory,
                Path.GetFileName(localPath),
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Report '{ReportName}' uploaded to {RemotePath}.", taskConfig.Name, remotePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute report '{ReportName}'.", taskConfig.Name);
            throw;
        }
    }

    private async Task<string> SaveToLocalFileAsync(ReportTaskOptions task, ReportRenderingResult result, System.Threading.CancellationToken cancellationToken)
    {
        var baseDirectory = _outputOptions.Value.Directory;
        var absoluteDirectory = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(absoluteDirectory);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safeName = string.Join("_", task.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var fileName = $"{safeName}_{timestamp}{result.FileExtension}";
        var targetPath = Path.Combine(absoluteDirectory, fileName);

        await File.WriteAllBytesAsync(targetPath, result.Content, cancellationToken).ConfigureAwait(false);
        return targetPath;
    }
}
