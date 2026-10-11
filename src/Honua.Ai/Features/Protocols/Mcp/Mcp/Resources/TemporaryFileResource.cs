// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;
using Honua.Geoprocessing;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp.Resources;

/// <summary>
/// Reads the exact temporary artifact URLs emitted by map rendering through the
/// canonical store, retaining its ownership and expiration checks. Never fetches
/// a caller-supplied URL or opens a caller-supplied filesystem path.
/// </summary>
internal sealed class TemporaryFileResource : IMcpResource
{
    public const string UriPrefix = "honua://temporary-files/";
    public const string Template = UriPrefix + "{fileId}";

    private readonly string _prefix;
    private readonly ILogger<TemporaryFileResource> _logger;

    public TemporaryFileResource(IOptions<TemporaryFileOptions> options, ILogger<TemporaryFileResource> logger)
    {
        _prefix = (options.Value.BaseUrl ?? "/temp").TrimEnd('/') + "/";
        _logger = logger;
    }

    public string Family => McpTelemetry.ResourceFamily.TemporaryFiles;

    public IReadOnlyList<McpResourceDescriptor> Describe() => [];

    public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() =>
    [
        new McpResourceTemplateDescriptor
        {
            UriTemplate = Template,
            Name = "Temporary rendered artifact",
            MimeType = "application/octet-stream",
            Description = "Owner-scoped rendered artifact; expires with the canonical temporary file. Binary reads are atomic and bounded by maxChars."
        }
    ];

    public bool CanHandle(string uri)
    {
        var fileName = GetFileName(uri);
        var dot = fileName.IndexOf('.');
        var token = dot < 0 ? fileName : fileName[..dot];
        // Both local GUIDs and shared-store opaque base64url tokens use this
        // alphabet. The canonical store resolves issued tokens and extensions.
        if (token.IsEmpty || token.Length > 128)
        {
            return false;
        }
        foreach (var character in token)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return false;
            }
        }

        return dot < 0 || fileName[dot..] is ".png" or ".jpg" or ".jpeg" or ".gif" or ".tif" or ".tiff" or ".pdf";
    }

    private ReadOnlySpan<char> GetFileName(string uri)
    {
        if (uri.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            return uri.AsSpan(UriPrefix.Length);
        }
        return uri.StartsWith(_prefix, StringComparison.Ordinal) ? uri.AsSpan(_prefix.Length) : [];
    }

    public async Task<McpResourcesReadResult> ReadAsync(HttpContext httpContext, string uri, CancellationToken cancellationToken)
    {
        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        if (!CanHandle(uri))
        {
            throw new GeoprocessingNotFoundException("Temporary artifact not found or expired.");
        }

        var store = httpContext.RequestServices.GetService<ITemporaryFileService>()
            ?? throw new GeoprocessingStoreUnavailableException("Temporary artifact storage is unavailable.");
        McpLog.ResourceRead(_logger, Family, uri);
        var file = await store.GetTemporaryFileAsync(GetFileName(uri).ToString(), principal, cancellationToken).ConfigureAwait(false)
            ?? throw new GeoprocessingNotFoundException("Temporary artifact not found or expired.");
        // Refuse before allocating base64 if the artifact cannot fit even an
        // explicit maximum-size read. Smaller request budgets are checked by
        // the dispatcher, without splitting a binary blob into invalid pieces.
        if (file.data.LongLength > McpPagination.MaxResourceReadChars / 4 * 3)
        {
            throw new GeoprocessingValidationException("Temporary artifact exceeds the MCP binary read limit; render a smaller image.");
        }

        return new McpResourcesReadResult
        {
            Contents = [new McpResourceContent { Uri = uri, MimeType = file.contentType, Blob = Convert.ToBase64String(file.data) }]
        };
    }
}
