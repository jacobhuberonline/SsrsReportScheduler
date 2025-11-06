namespace SsrsReportScheduler.Options;

public sealed class BedrockOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}
