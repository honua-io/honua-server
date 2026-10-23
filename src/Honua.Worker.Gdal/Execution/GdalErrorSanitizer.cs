// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Sanitizes GDAL CLI stderr before it is persisted onto a job's
/// client-visible <c>ErrorMessage</c>. Client text must not leak scratch
/// workspace paths, other absolute paths, <c>/vsi*</c> object keys, or
/// configured S3/Azure endpoint hosts. Full unsanitized stderr is preserved
/// in structured logs via <see cref="TruncateForLog"/>.
/// </summary>
internal static partial class GdalErrorSanitizer
{
    private const string EndpointPlaceholder = "<endpoint>";
    private static string[] _configuredHosts = [];

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?i)(?<![>\w])/vsi[a-z0-9]+/[^\s""'<>|]+", RegexOptions.CultureInvariant)]
    private static partial Regex VsiRegex();

    [GeneratedRegex(
        @"(?i)\b(?:[a-z0-9-]+\.)+(?:internal|local|amazonaws\.com|blob\.core\.windows\.net)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex EndpointHostRegex();

    /// <summary>
    /// Records endpoint hosts from the worker's S3 and Azure options so later
    /// <see cref="Sanitize"/> calls redact them without each executor threading
    /// the options through. Operator logs stay unredacted.
    /// </summary>
    public static void UseConfiguredEndpoints(AwsS3Options? s3, AzureBlobOptions? azure)
        => _configuredHosts = ExtractHosts(s3, azure);

    /// <summary>
    /// Returns a sanitized, length-capped stderr suitable for the job's
    /// client-visible error message. Replaces the per-job workspace path with
    /// <c>&lt;scratch&gt;</c>, other absolute and <c>/vsi*</c> paths with
    /// <c>&lt;path&gt;</c>, and endpoint URLs or hosts with <c>&lt;endpoint&gt;</c>.
    /// </summary>
    /// <param name="stderr">Raw GDAL stderr.</param>
    /// <param name="workspace">Per-job scratch workspace path.</param>
    /// <param name="endpointHosts">
    /// Extra endpoint hosts to redact, in addition to hosts recorded by
    /// <see cref="UseConfiguredEndpoints"/> and the built-in cloud host suffixes.
    /// </param>
    public static string Sanitize(string stderr, string workspace, IReadOnlyList<string>? endpointHosts = null)
    {
        var redacted = WorkspacePathSanitizer.SanitizeForClient(stderr, workspace, int.MaxValue);
        if (redacted.Length == 0)
        {
            return string.Empty;
        }

        redacted = VsiRegex().Replace(redacted, WorkspacePathSanitizer.PathPlaceholder);
        redacted = UrlRegex().Replace(redacted, EndpointPlaceholder);
        redacted = EndpointHostRegex().Replace(redacted, EndpointPlaceholder);
        redacted = RedactHosts(redacted, _configuredHosts);
        redacted = RedactHosts(redacted, endpointHosts);

        return redacted.Length <= WorkspacePathSanitizer.DefaultMaxLength
            ? redacted
            : redacted[..WorkspacePathSanitizer.DefaultMaxLength] + "…";
    }

    /// <summary>
    /// Trims and length-caps unredacted stderr for the executor's operator-facing
    /// structured log. Unlike <see cref="Sanitize"/> this preserves scratch paths,
    /// virtual-file paths, and endpoint hosts.
    /// </summary>
    public static string TruncateForLog(string stderr)
        => WorkspacePathSanitizer.TruncateForLog(stderr);

    private static string RedactHosts(string text, IReadOnlyList<string>? hosts)
    {
        if (hosts is null || hosts.Count == 0 || text.Length == 0)
        {
            return text;
        }

        foreach (var host in hosts)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length < 4)
            {
                continue;
            }

            text = text.Replace(host, EndpointPlaceholder, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private static string[] ExtractHosts(AwsS3Options? s3, AzureBlobOptions? azure)
    {
        var hosts = new List<string>();
        AddUriHost(hosts, s3?.ServiceUrl);
        AddAzureHosts(hosts, azure?.ConnectionString);
        return hosts.ToArray();
    }

    private static void AddUriHost(List<string> hosts, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(uri.Host))
        {
            hosts.Add(uri.Host);
        }
    }

    private static void AddAzureHosts(List<string> hosts, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        string? account = null;
        string? suffix = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key.Equals("BlobEndpoint", StringComparison.OrdinalIgnoreCase))
            {
                AddUriHost(hosts, value);
            }
            else if (key.Equals("AccountName", StringComparison.OrdinalIgnoreCase))
            {
                account = value;
            }
            else if (key.Equals("EndpointSuffix", StringComparison.OrdinalIgnoreCase))
            {
                suffix = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(suffix))
        {
            hosts.Add(account + "." + suffix);
        }
    }
}
