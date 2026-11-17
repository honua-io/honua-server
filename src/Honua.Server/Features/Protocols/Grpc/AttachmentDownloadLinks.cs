// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.Grpc;

// Short-lived signed download links for attachments kept in local file storage, which has no
// presigned URLs of its own. The caller's read access is checked when the link is issued.
internal sealed class AttachmentDownloadLinks(IDataProtectionProvider protection)
{
    internal const string RoutePrefix = "/api/v1/attachments/downloads/";

    private readonly ITimeLimitedDataProtector _protector = protection
        .CreateProtector("Honua.AttachmentDownloadLinks.v1")
        .ToTimeLimitedDataProtector();

    // Builds an absolute link on the server's public base URL, or returns null when no
    // acceptable base URL is known. Plain HTTP is used only for a request that arrived over HTTP.
    public string? CreateUrl(HttpContext context, AttachmentDownloadTarget target, DateTimeOffset expires)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) ||
            !(origin.Scheme == Uri.UriSchemeHttps || (origin.Scheme == Uri.UriSchemeHttp && !context.Request.IsHttps)))
        {
            return null;
        }
        return string.Concat(baseUrl.TrimEnd('/'), RoutePrefix, Issue(target, expires));
    }

    internal string Issue(AttachmentDownloadTarget target, DateTimeOffset expires)
        => _protector.Protect(target.Encode(), expires);

    internal AttachmentDownloadTarget? Read(string token)
    {
        try
        {
            return AttachmentDownloadTarget.Decode(_protector.Unprotect(token));
        }
        // Tampered, expired, foreign-key and malformed tokens all mean "no such link".
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

internal sealed record AttachmentDownloadTarget(int LayerId, long FeatureId, long AttachmentId, string StoragePath)
{
    internal string Encode() => string.Join('\n',
        LayerId.ToString(CultureInfo.InvariantCulture),
        FeatureId.ToString(CultureInfo.InvariantCulture),
        AttachmentId.ToString(CultureInfo.InvariantCulture),
        StoragePath);

    internal static AttachmentDownloadTarget? Decode(string value)
    {
        var parts = value.Split('\n', 4);
        return parts.Length == 4 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var layerId) &&
            long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var featureId) &&
            long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var attachmentId)
            ? new AttachmentDownloadTarget(layerId, featureId, attachmentId, parts[3])
            : null;
    }
}

internal static class AttachmentDownloadEndpoints
{
    public static IEndpointRouteBuilder MapAttachmentDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(AttachmentDownloadLinks.RoutePrefix + "{token}", HandleDownload)
            .WithName("DownloadSignedAttachment")
            .WithDisplayName("Signed Attachment Download")
            .WithSummary("Download an attachment through a signed link")
            .WithDescription("Serves the attachment named by a short-lived link that gRPC GetAttachmentUrl issues for local file storage")
            .WithTags("Attachments")
            // The signed token is the credential; the caller's read access was checked when it was issued.
            .AllowAnonymous()
            .CacheOutput(policy => policy.NoCache());
        return endpoints;
    }

    private static async Task<IResult> HandleDownload(
        string token,
        HttpContext context,
        [FromServices] AttachmentDownloadLinks links,
        [FromServices] IAttachmentStore attachments,
        CancellationToken cancellationToken)
    {
        if (links.Read(token) is not { } target)
        {
            return TypedResults.NotFound();
        }
        var content = await attachments.DownloadAsync(target.LayerId, target.FeatureId, target.AttachmentId, cancellationToken)
            .ConfigureAwait(false);
        if (content is not { } found || !string.Equals(found.Attachment.StoragePath, target.StoragePath, StringComparison.Ordinal))
        {
            if (content is { } stale)
            {
                await stale.Content.DisposeAsync().ConfigureAwait(false);
            }
            return TypedResults.NotFound();
        }
        // The stored content type is client-supplied; forbid sniffing and force a download.
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(found.Content, found.Attachment.ContentType, found.Attachment.Filename, enableRangeProcessing: true);
    }
}
