using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SsrsReportScheduler.Models;
using SsrsReportScheduler.Options;

namespace SsrsReportScheduler.Services;

public sealed class SsrsReportClient
{
    private const string ReportingServicesNs = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices";
    private static readonly XNamespace ExecutionNs = ReportingServicesNs;

    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace XsdNs = "http://www.w3.org/2001/XMLSchema";

    private enum SoapVersion { V11, V12 }

    private static XNamespace Soap11Ns = "http://schemas.xmlsoap.org/soap/envelope/";
    private static XNamespace Soap12Ns = "http://www.w3.org/2003/05/soap-envelope";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<ReportServerOptions> _options;
    private readonly ILogger<SsrsReportClient> _logger;

    public const string HttpClientName = "SsrsReportExecution";

    public SsrsReportClient(
        IHttpClientFactory httpClientFactory,
        IOptions<ReportServerOptions> options,
        ILogger<SsrsReportClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<ReportRenderingResult> ExecuteAsync(ReportTaskOptions task, CancellationToken ct)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));

        var cfg = _options.Value;
        var reportPath = cfg.FolderPath + "/" + task.Name;
        var execId = await LoadReportAsync(reportPath, ct);

        var format = ReportFormatMapper.Parse(task.Format);

        if (task.Parameters?.Count > 0)
            await SetExecutionParametersAsync(execId, task.Parameters!, ct);

        return await RenderReportAsync(execId, format, ct);
    }

    private async Task<string> LoadReportAsync(string reportPath, CancellationToken ct)
    {
        _logger.LogInformation("Loading SSRS report '{ReportPath}'.", reportPath);

        var body = new XElement(ExecutionNs + "LoadReport",
            new XElement(ExecutionNs + "Report", reportPath));

        var resp = await SendSoapRequestAsync("LoadReport", body, executionId: null, ct)
            .ConfigureAwait(false);

        var execId = resp.Descendants(ExecutionNs + "ExecutionID").Select(x => x.Value).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(execId))
            throw new InvalidOperationException("SSRS did not return an execution identifier.");

        _logger.LogDebug("Received SSRS execution ID {ExecutionId}.", execId);
        return execId;
    }

    private async Task SetExecutionParametersAsync(string executionId, IDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Setting {ParameterCount} parameters for execution {ExecutionId}.", parameters.Count, executionId);

        var parameterValues = parameters
            .Select(kvp => new XElement(ExecutionNs + "ParameterValue",
                new XElement(ExecutionNs + "Name", kvp.Key),
                new XElement(ExecutionNs + "Value", kvp.Value)))
            .ToArray();

        var body = new XElement(ExecutionNs + "SetExecutionParameters",
            new XElement(ExecutionNs + "Parameters", parameterValues),
            new XElement(ExecutionNs + "ParameterLanguage", CultureInfo.InvariantCulture.Name));

        await SendSoapRequestAsync(
            "SetExecutionParameters",
            body,
            executionId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReportRenderingResult> RenderReportAsync(string executionId, ReportRenderFormat format, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Rendering execution {ExecutionId} as {Format}.", executionId, format);

        var ssrsFormat = ReportFormatMapper.ToSsrsFormat(format);

        var body = new XElement(ExecutionNs + "Render",
            new XElement(ExecutionNs + "Format", ssrsFormat),
            new XElement(ExecutionNs + "DeviceInfo"));

        var response = await SendSoapRequestAsync(
            "Render",
            body,
            executionId,
            cancellationToken).ConfigureAwait(false);

        var resultBase64 = response
            .Descendants(ExecutionNs + "Result")
            .Select(x => x.Value)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(resultBase64))
        {
            throw new InvalidOperationException("SSRS did not return any report content.");
        }

        var bytes = Convert.FromBase64String(resultBase64);

        var extension = response
            .Descendants(ExecutionNs + "Extension")
            .Select(x => x.Value)
            .FirstOrDefault();

        var mimeType = response
            .Descendants(ExecutionNs + "MimeType")
            .Select(x => x.Value)
            .FirstOrDefault();

        var fileExtension = !string.IsNullOrWhiteSpace(extension)
            ? $".{extension.Trim('.')}"
            : ReportFormatMapper.GetFileExtension(format);

        var resolvedMimeType = !string.IsNullOrWhiteSpace(mimeType)
            ? mimeType
            : ReportFormatMapper.GetMimeType(format);

        return new ReportRenderingResult(bytes, format, fileExtension, resolvedMimeType);
    }

    private async Task<XDocument> SendSoapRequestAsync(string operation, XElement body, string? executionId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var serviceUrl = $"{_options.Value.BaseUrl.TrimEnd('/')}/{_options.Value.ExecutionEndpoint.TrimStart('/')}";
        var soapAction = $"{ExecutionNs.NamespaceName}/{operation}";

        var env11 = BuildEnvelope(body, executionId, SoapVersion.V11);
        using (var req11 = new HttpRequestMessage(HttpMethod.Post, serviceUrl)
        { Content = new StringContent(env11.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") })
        {
            req11.Headers.TryAddWithoutValidation("SOAPAction", $"\"{soapAction}\"");
            _logger.LogInformation("SSRS SOAP11 POST => {Url} (Action={Action})", req11.RequestUri, soapAction);

            using var resp11 = await client.SendAsync(req11, HttpCompletionOption.ResponseHeadersRead, ct);
            var xml11 = await resp11.Content.ReadAsStringAsync(ct);

            if (resp11.IsSuccessStatusCode) return XDocument.Parse(xml11);

            if (!xml11.Contains("SOAPAction", StringComparison.OrdinalIgnoreCase))
                throw BuildHttpException(resp11, xml11); // different error -> surface it
            _logger.LogWarning("SOAPAction not recognized. Retrying with SOAP 1.2…");
        }

        var env12 = BuildEnvelope(body, executionId, SoapVersion.V12);
        var payload12 = env12.ToString(SaveOptions.DisableFormatting);
        using (var content12 = new StringContent(payload12, Encoding.UTF8))
        {
            content12.Headers.Clear();
            content12.Headers.TryAddWithoutValidation("Content-Type", $"application/soap+xml; charset=utf-8; action=\"{soapAction}\"");

            using var req12 = new HttpRequestMessage(HttpMethod.Post, serviceUrl) { Content = content12 };
            _logger.LogInformation("SSRS SOAP12 POST => {Url} (Action={Action})", req12.RequestUri, soapAction);

            using var resp12 = await client.SendAsync(req12, HttpCompletionOption.ResponseHeadersRead, ct);
            var xml12 = await resp12.Content.ReadAsStringAsync(ct);

            if (resp12.IsSuccessStatusCode) return XDocument.Parse(xml12);

            throw BuildHttpException(resp12, xml12);
        }
    }

    static Exception BuildHttpException(HttpResponseMessage resp, string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var fault = doc.Descendants(XName.Get("Fault", "http://schemas.xmlsoap.org/soap/envelope/")).FirstOrDefault()
                    ?? doc.Descendants(XName.Get("Fault", "http://www.w3.org/2003/05/soap-envelope")).FirstOrDefault();
            if (fault != null)
            {
                var faultString = fault.Element("faultstring")?.Value
                               ?? fault.Element(XName.Get("Reason", "http://www.w3.org/2003/05/soap-envelope"))?.Value
                               ?? "SOAP Fault";
                var detail = fault.Element("detail")?.Value;
                return new InvalidOperationException($"SSRS SOAP Fault: {faultString}. {detail}");
            }
        }
        catch { /* ignore */ }

        return new HttpRequestException($"HTTP {(int)resp.StatusCode} from SSRS. Body: {body}");
    }

    private static XDocument BuildEnvelope(XElement body, string? executionId, SoapVersion ver)
    {
        var soapNs = ver == SoapVersion.V12 ? Soap12Ns : Soap11Ns;

        var header = executionId is null ? null
            : new XElement(soapNs + "Header",
                new XElement(ExecutionNs + "ExecutionHeader",
                    new XElement(ExecutionNs + "ExecutionID", executionId)));

        var envelope = new XElement(soapNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", soapNs),
            new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
            new XAttribute(XNamespace.Xmlns + "xsd", XsdNs),
            header,
            new XElement(soapNs + "Body", body));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), envelope);
    }
}
