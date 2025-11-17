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
builder.Services.Configure<SystemOptionsApiOptions>(builder.Configuration.GetSection("SystemOptionsApi"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));

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

builder.Services.AddHttpClient(SystemOptionsApiClient.HttpClientName)
    .ConfigureHttpClient((sp, client) =>
    {
        var o = sp.GetRequiredService<IOptions<SystemOptionsApiOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(o.BaseUrl))
        {
            client.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
        }

        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds));
    });

builder.Services.AddSingleton<SsrsReportClient>();
builder.Services.AddSingleton<SftpUploader>();
builder.Services.AddSingleton<CronConverter>();
builder.Services.AddSingleton<SystemOptionsApiClient>();
builder.Services.AddSingleton<IReportTaskSource, ApiBackedReportTaskSource>();
builder.Services.AddSingleton<EmailSender>();

builder.Services.AddQuartz(q =>
{
    using var scope = builder.Services.BuildServiceProvider();
    var cronConverter = scope.GetRequiredService<CronConverter>();
    var reportTaskSource = scope.GetRequiredService<IReportTaskSource>();
    var loggerFactory = scope.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("SchedulerConfiguration");

    IReadOnlyList<ReportTaskOptions> tasks;
    try
    {
        tasks = reportTaskSource.GetReportTasksAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unable to load report tasks. No Quartz jobs will be scheduled.");
        return;
    }

    foreach (var task in tasks)
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
            logger.LogError(ex, "Error converting cron description for task {TaskName}.", task.Name);
            continue;
        }

        if (!CronExpression.IsValidExpression(cronExpression))
        {
            logger.LogError("Skipping task {TaskName} due to invalid cron expression '{CronExpression}'.", task.Name, cronExpression);
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

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Startup")
    .LogInformation("SSRS report scheduler is running. Press Ctrl+C to exit.");

await host.RunAsync();
