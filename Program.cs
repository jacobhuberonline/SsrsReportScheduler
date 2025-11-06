using System.Net;
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

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Services.Configure<ReportServerOptions>(builder.Configuration.GetSection("ReportServer"));
builder.Services.Configure<OutputOptions>(builder.Configuration.GetSection("Output"));
builder.Services.Configure<SftpOptions>(builder.Configuration.GetSection("Sftp"));
builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection("Scheduler"));
builder.Services.Configure<BedrockOptions>(builder.Configuration.GetSection("Bedrock"));

builder.Services.AddHttpClient(SsrsReportClient.HttpClientName)
    .ConfigureHttpClient((sp, client) =>
    {
        var o = sp.GetRequiredService<IOptions<ReportServerOptions>>().Value;
        client.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/"); // ensures trailing slash
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds));
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var o = sp.GetRequiredService<IOptions<ReportServerOptions>>().Value;
        var creds = string.IsNullOrWhiteSpace(o.Domain)
            ? new NetworkCredential(o.Username, o.Password)
            : new NetworkCredential(o.Username, o.Password, o.Domain);

        return new HttpClientHandler
        {
            PreAuthenticate = true,
            Credentials = creds,
            UseCookies = true,
            AllowAutoRedirect = true
        };
    });

builder.Services.AddSingleton<SsrsReportClient>();
builder.Services.AddSingleton<SftpUploader>();
builder.Services.AddSingleton<CronConverter>();

builder.Services.AddQuartz(q =>
{
    var cronConverter = builder.Services.BuildServiceProvider().GetRequiredService<CronConverter>();
    var schedulerOptions = builder.Configuration.GetSection("Scheduler").Get<SchedulerOptions>() ?? new SchedulerOptions();
    var invalidCronExpressions = new List<(string TaskName, string CronExpression)>();

    foreach (var task in schedulerOptions.ReportTasks)
    {
        if (string.IsNullOrWhiteSpace(task.Name))
        {
            continue;
        }

        string cronExpression = task.CronExpression;
        try
        {
            cronExpression = cronConverter.ConvertDescriptionToCronAsync(task.CronExpression).Result;
        }
        catch (Exception ex)
        {
            var logger = builder.Services.BuildServiceProvider().GetRequiredService<ILoggerFactory>().CreateLogger("CronConverter");
            logger.LogError($"Error converting cron description for task {task.Name}: {ex.Message}");
            continue;
        }

        if (!CronExpression.IsValidExpression(cronExpression))
        {
            invalidCronExpressions.Add((task.Name, cronExpression));
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
            .WithCronSchedule(cronExpression, cron => cron.InTimeZone(TimeZoneInfo.Local)));
    }
});

builder.Services.AddQuartzHostedService(options =>
{
    options.WaitForJobsToComplete = true;
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var host = builder.Build();

var invalidCronExpressions = new List<(string TaskName, string CronExpression)>();
if (invalidCronExpressions.Count > 0)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Configuration");
    foreach (var (taskName, cronExpression) in invalidCronExpressions)
    {
        logger.LogError("Skipping task {TaskName} due to invalid cron expression '{CronExpression}'.", taskName, cronExpression);
    }
}

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Startup")
    .LogInformation("SSRS report scheduler is running. Press Ctrl+C to exit.");

await host.RunAsync();
