using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using SsrsReportScheduler.Options;

namespace SsrsReportScheduler.Services;

/// <summary>
/// Wraps SSH.NET SFTP operations and shields the rest of the app from connection management.
/// </summary>
public sealed class SftpUploader
{
    private readonly IOptions<SftpOptions> _options;
    private readonly ILogger<SftpUploader> _logger;

    public SftpUploader(IOptions<SftpOptions> options, ILogger<SftpUploader> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<string> UploadAsync(byte[] content, string? overrideDirectory, string remoteFileName, CancellationToken cancellationToken)
    {
        var settings = _options.Value;
        var targetDirectory = NormalizeRemotePath(string.IsNullOrWhiteSpace(overrideDirectory) ? settings.RemoteDirectory : overrideDirectory);
        var remotePath = CombineRemotePath(targetDirectory, remoteFileName);

        _logger.LogInformation(
            "Uploading report to SFTP host {Host}:{Port} at {RemotePath}.",
            settings.Host,
            settings.Port,
            remotePath);

        await Task.Run(() =>
        {
            using var client = CreateClient(settings);
            client.Connect();

            EnsureRemoteDirectory(client, targetDirectory);

            using var stream = new MemoryStream(content);
            client.UploadFile(stream, remotePath, true);

            client.Disconnect();
        }, cancellationToken).ConfigureAwait(false);

        return remotePath;
    }

    private static SftpClient CreateClient(SftpOptions options) =>
        new(options.Host, options.Port, options.Username, options.Password);

    private static void EnsureRemoteDirectory(SftpClient client, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory == "/")
        {
            return;
        }

        var segments = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        foreach (var segment in segments)
        {
            current += "/" + segment;
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
    }

    private static string CombineRemotePath(string directory, string fileName)
    {
        var sanitizedDirectory = NormalizeRemotePath(directory);
        return sanitizedDirectory.EndsWith("/", StringComparison.Ordinal)
            ? sanitizedDirectory + fileName
            : sanitizedDirectory + "/" + fileName;
    }

    private static string NormalizeRemotePath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "/";
        }

        var trimmed = directory.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.Replace("//", "/", StringComparison.Ordinal);
    }
}
