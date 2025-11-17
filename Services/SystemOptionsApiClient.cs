using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SsrsReportScheduler.Models;
using SsrsReportScheduler.Options;

namespace SsrsReportScheduler.Services;

public sealed class SystemOptionsApiClient
{
    public const string HttpClientName = "SystemOptionsApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<SystemOptionsApiOptions> _options;
    private readonly ILogger<SystemOptionsApiClient> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SystemOptionsApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<SystemOptionsApiOptions> options,
        ILogger<SystemOptionsApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<SystemOptionsSnapshot?> TryGetSystemOptionsAsync(CancellationToken cancellationToken)
    {
        var settings = _options.Value;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            _logger.LogDebug("System options API base URL not configured; skipping remote fetch.");
            return null;
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        var requestUri = settings.SystemOptionsPath?.Trim();
        if (string.IsNullOrEmpty(requestUri))
        {
            requestUri = "/api/system/options";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var headerName = string.IsNullOrWhiteSpace(settings.ApiKeyHeaderName)
                ? "X-Api-Key"
                : settings.ApiKeyHeaderName;

            if (!request.Headers.TryAddWithoutValidation(headerName, settings.ApiKey))
            {
                _logger.LogWarning("Unable to add API key header '{HeaderName}' to system options request.", headerName);
            }
        }

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "System options API request failed with status {StatusCode}. Response body: {Body}",
                    (int)response.StatusCode,
                    body);
                return null;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<SystemOptionsDto>(contentStream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (dto is null)
            {
                _logger.LogWarning("System options API returned an empty payload.");
                return null;
            }

            return MapSnapshot(dto);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch system options from API.");
            return null;
        }
    }

    public async Task<IReadOnlyList<ReportTaskOptions>> TryGetReportTasksAsync(CancellationToken cancellationToken)
    {
        var settings = _options.Value;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            _logger.LogDebug("System options API base URL not configured; skipping report task fetch.");
            return Array.Empty<ReportTaskOptions>();
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        var path = settings.ReportTasksPath?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            path = "/api/system/options/report-tasks";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var headerName = string.IsNullOrWhiteSpace(settings.ApiKeyHeaderName)
                ? "X-Api-Key"
                : settings.ApiKeyHeaderName;

            request.Headers.TryAddWithoutValidation(headerName, settings.ApiKey);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "System options API report task request failed with status {StatusCode}. Response body: {Body}",
                    (int)response.StatusCode,
                    body);
                return Array.Empty<ReportTaskOptions>();
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<ReportTaskDefinitionDto[]>(contentStream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (dto is null || dto.Length == 0)
            {
                _logger.LogInformation("System options API returned no report tasks.");
                return Array.Empty<ReportTaskOptions>();
            }

            return dto.Select(MapReportTask).Where(task => task is not null).OfType<ReportTaskOptions>().ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch report tasks from system options API.");
            return Array.Empty<ReportTaskOptions>();
        }
    }

    private static SystemOptionsSnapshot MapSnapshot(SystemOptionsDto dto)
    {
        var reportTasks = dto.ReportTasks?.Select(MapReportTask).Where(task => task is not null).OfType<ReportTaskOptions>().ToArray()
            ?? Array.Empty<ReportTaskOptions>();

        return new SystemOptionsSnapshot(reportTasks, DateTimeOffset.UtcNow);
    }

    private static ReportTaskOptions? MapReportTask(ReportTaskDefinitionDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            return null;
        }

        var delivery = dto.Delivery ?? new ReportDeliveryOptions();
        delivery.Email ??= new EmailDeliveryOptions();
        delivery.Sftp ??= new SftpDeliveryOptions();

        var task = new ReportTaskOptions
        {
            Name = dto.Name.Trim(),
            Format = string.IsNullOrWhiteSpace(dto.Format) ? "PDF" : dto.Format.Trim(),
            CronExpression = string.IsNullOrWhiteSpace(dto.CronExpression) ? "0 0 6 * * ?" : dto.CronExpression.Trim(),
            Parameters = dto.Parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Delivery = delivery
        };

        return task;
    }

    private sealed class SystemOptionsDto
    {
        public IReadOnlyList<ReportTaskDefinitionDto>? ReportTasks { get; init; }
    }

    private sealed class ReportTaskDefinitionDto
    {
        public string? Name { get; init; }
        public string? Format { get; init; }
        public string? CronExpression { get; init; }
        public Dictionary<string, string>? Parameters { get; init; }
        public ReportDeliveryOptions? Delivery { get; init; }
    }
}
