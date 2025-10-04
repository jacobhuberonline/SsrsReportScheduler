using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using SsrsReportScheduler.Jobs;
using SsrsReportScheduler.Options;
using SsrsReportScheduler.Services;

var builder = Host.CreateApplicationBuilder(args);

// Ensure configuration files are loaded so that options binding works when scheduling jobs.
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Services.Configure<ReportServerOptions>(builder.Configuration.GetSection("ReportServer"));
builder.Services.Configure<OutputOptions>(builder.Configuration.GetSection("Output"));
builder.Services.Configure<SftpOptions>(builder.Configuration.GetSection("Sftp"));
builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection("Scheduler"));

builder.Services.AddHttpClient(SsrsReportClient.HttpClientName)
    .ConfigureHttpClient((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<ReportServerOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException("ReportServer:BaseUrl must be configured.");
        }

        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var options = sp.GetRequiredService<IOptions<ReportServerOptions>>().Value;
        var credentials = string.IsNullOrWhiteSpace(options.Domain)
            ? new NetworkCredential(options.Username, options.Password)
            : new NetworkCredential(options.Username, options.Password, options.Domain);

        return new HttpClientHandler
        {
            PreAuthenticate = true,
            Credentials = credentials
        };
    });

builder.Services.AddSingleton<SsrsReportClient>();
builder.Services.AddSingleton<SftpUploader>();

var invalidCronExpressions = new List<(string TaskName, string CronExpression)>();

builder.Services.AddQuartz(q =>
{
    // Materialise the configured jobs up-front so that Quartz can register them during startup.
    var schedulerOptions = builder.Configuration.GetSection("Scheduler").Get<SchedulerOptions>() ?? new SchedulerOptions();
    foreach (var task in schedulerOptions.ReportTasks)
    {
        if (string.IsNullOrWhiteSpace(task.Name))
        {
            continue;
        }

        if (!CronExpression.IsValidExpression(task.CronExpression))
        {
            invalidCronExpressions.Add((task.Name, task.CronExpression));
            continue;
        }

        var jobKey = new JobKey(task.Name);
        q.AddJob<ReportExecutionJob>(opts =>
        {
            opts.WithIdentity(jobKey);
            opts.StoreDurably();
            opts.UsingJobData(ReportExecutionJob.TaskNameKey, task.Name);
        });

        q.AddTrigger(trigger => trigger
            .ForJob(jobKey)
            .WithIdentity($"{task.Name}.trigger")
            .WithCronSchedule(task.CronExpression, cron => cron.InTimeZone(TimeZoneInfo.Local)));
    }
});

builder.Services.AddQuartzHostedService(options =>
{
    options.WaitForJobsToComplete = true;
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var host = builder.Build();

// Report any schedules that were skipped because they are invalid.
if (invalidCronExpressions.Count > 0)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Configuration");
    foreach (var (taskName, cronExpression) in invalidCronExpressions)
    {
        logger.LogError("Skipping task {TaskName} due to invalid cron expression '{CronExpression}'.", taskName, cronExpression);
    }
}

// Inform the operator that the scheduler has started successfully.
host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Startup")
    .LogInformation("SSRS report scheduler is running. Press Ctrl+C to exit.");

await host.RunAsync();
