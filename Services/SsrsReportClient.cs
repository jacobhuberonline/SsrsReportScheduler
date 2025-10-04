using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SsrsReportScheduler.Models;
using SsrsReportScheduler.Options;

namespace SsrsReportScheduler.Services;

/// <summary>
/// Handles SSRS SOAP communication for loading, parameterising, and rendering reports.
/// </summary>
public sealed class SsrsReportClient
{
    private const string SoapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string ReportExecutionNamespace = "http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportexecution2005";
    private const string ReportingServicesNamespace = "http://schemas.microsoft.com/sqlserver/2003/10/ReportingServices";

    private static readonly XNamespace SoapNs = SoapEnvelopeNamespace;
    private static readonly XNamespace ExecutionNs = ReportExecutionNamespace;
    private static readonly XNamespace RsNs = ReportingServicesNamespace;
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace XsdNs = "http://www.w3.org/2001/XMLSchema";

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

    public async Task<ReportRenderingResult> ExecuteAsync(ReportTaskOptions task, CancellationToken cancellationToken)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        var renderFormat = ReportFormatMapper.Parse(task.Format);
        var executionId = await LoadReportAsync(task.ReportPath, cancellationToken).ConfigureAwait(false);

        if (task.Parameters?.Count > 0)
        {
            await SetExecutionParametersAsync(executionId, task.Parameters!, cancellationToken).ConfigureAwait(false);
        }

        var renderResult = await RenderReportAsync(executionId, renderFormat, cancellationToken).ConfigureAwait(false);
        return renderResult;
    }

    private async Task<string> LoadReportAsync(string reportPath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Loading SSRS report '{ReportPath}'.", reportPath);

        var body = new XElement(ExecutionNs + "LoadReport",
            new XElement(ExecutionNs + "Report", reportPath),
            new XElement(ExecutionNs + "HistoryID",
                new XAttribute(XsiNs + "nil", true)));

        var response = await SendSoapRequestAsync(
            "LoadReport",
            body,
            executionId: null,
            cancellationToken).ConfigureAwait(false);

        var executionId = response
            .Descendants(ExecutionNs + "ExecutionID")
            .Select(x => x.Value)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(executionId))
        {
            throw new InvalidOperationException("SSRS did not return an execution identifier.");
        }

        _logger.LogDebug("Received SSRS execution ID {ExecutionId}.", executionId);
        return executionId;
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

    private async Task<XDocument> SendSoapRequestAsync(
        string operation,
        XElement body,
        string? executionId,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var envelope = BuildEnvelope(body, executionId);
        var xmlPayload = envelope.ToString(SaveOptions.DisableFormatting);
        using var content = new StringContent(xmlPayload, Encoding.UTF8, "text/xml");

        var soapAction = $"{ReportExecutionNamespace}/{operation}";
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Value.ExecutionEndpoint)
        {
            Content = content
        };
        request.Headers.Add("SOAPAction", soapAction);

        _logger.LogDebug("Sending SSRS SOAP request {Operation}.", operation);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseXml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return XDocument.Parse(responseXml);
    }

    private static XDocument BuildEnvelope(XElement body, string? executionId)
    {
        var header = executionId is null
            ? null
            : new XElement(SoapNs + "Header",
                new XElement(ExecutionNs + "ExecutionHeader",
                    new XElement(ExecutionNs + "ExecutionID", executionId)));

        var envelope = new XElement(SoapNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
            new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
            new XAttribute(XNamespace.Xmlns + "xsd", XsdNs),
            header,
            new XElement(SoapNs + "Body", body));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), envelope);
    }
}
