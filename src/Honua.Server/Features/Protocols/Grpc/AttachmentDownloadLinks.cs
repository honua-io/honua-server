// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Infrastructure.Helpers;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.Server.Features.Protocols.Grpc;

// Short-lived signed download links for attachments kept in local file storage, which has no
// presigned URLs of its own. The caller's read access is checked when the link is issued; like a
// cloud presigned URL, an issued link keeps working until it expires.
internal sealed class AttachmentDownloadLinks(IDataProtectionProvider protection)
{
    internal const string RoutePrefix = "/api/v1/attachments/downloads/";

    private readonly ITimeLimitedDataProtector _protector = protection
        .CreateProtector("Honua.AttachmentDownloadLinks.v1")
        .ToTimeLimitedDataProtector();

    // Builds an absolute link on the configured public base URL, or else on the origin this request
    // called (the link goes back only to this caller, so its own Host is safe to echo). Returns null
    // when no acceptable origin is known. Plain HTTP is used only for a request that arrived over HTTP.
    public string? CreateUrl(HttpContext context, AttachmentDownloadTarget target, DateTimeOffset expires)
    {
        var request = context.Request;
        var baseUrl = BaseUrlResolver.TryGetConfiguredBaseUrl(
            context.RequestServices.GetRequiredService<IConfiguration>(), out var configured)
            ? configured
            : $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) ||
            !(origin.Scheme == Uri.UriSchemeHttps || (origin.Scheme == Uri.UriSchemeHttp && !request.IsHttps)))
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

internal sealed record AttachmentDownloadTarget
{
    public required int LayerId { get; init; }

    public required long FeatureId { get; init; }

    public required long AttachmentId { get; init; }

    public required string StoragePath { get; init; }

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
            ? new AttachmentDownloadTarget
            {
                LayerId = layerId,
                FeatureId = featureId,
                AttachmentId = attachmentId,
                StoragePath = parts[3]
            }
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

    private static async Task<IResult> HandleDownload(string token, HttpContext context)
    {
        var services = context.RequestServices;
        if (services.GetRequiredService<AttachmentDownloadLinks>().Read(token) is not { } target)
        {
            return TypedResults.NotFound();
        }
        var content = await services.GetRequiredService<IAttachmentStore>()
            .DownloadAsync(target.LayerId, target.FeatureId, target.AttachmentId, context.RequestAborted)
            .ConfigureAwait(false);
        if (content is not { } found || !string.Equals(found.Attachment.StoragePath, target.StoragePath, StringComparison.Ordinal))
        {
            if (content is { } stale)
            {
                await stale.Content.DisposeAsync().ConfigureAwait(false);
            }
            return TypedResults.NotFound();
        }
        // The stored content type is client-supplied; forbid sniffing and force a download. The link
        // is a short-lived credential, so no shared cache may keep the bytes past its expiry.
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.File(found.Content, found.Attachment.ContentType, found.Attachment.Filename, enableRangeProcessing: true);
    }
}
