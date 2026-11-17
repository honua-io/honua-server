// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.Server.Features.Protocols.Grpc;

// Storage without presigned URLs (local file storage on a Compose install) still gets a native
// download: GetAttachmentUrl returns a short-lived link to this server. The token, protected by the
// server's data-protection key ring, names one attachment and expires with the URL. It is minted only
// after the gRPC caller passed the attachment read check, so the token itself authorizes the download.
// Like a presigned URL it is a bearer credential for its lifetime, including where access logs record paths.
internal static class AttachmentDownloadLinks
{
    public const string Route = "/api/v1/attachments/downloads/{token}";

    private const string Purpose = "Honua.Grpc.AttachmentDownload.v1";

    public static string Create(HttpContext context, AttachmentDownloadTarget target, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = Protector(context.RequestServices.GetRequiredService<IDataProtectionProvider>())
            .Protect(target.ToString(), lifetime);
        // The scheme is the caller's own as the server sees it: behind a TLS-terminating proxy that
        // needs ForwardedHeaders:Enabled, or the link is plain HTTP like the request that reached us.
        var request = context.Request;
        return $"{request.Scheme}://{request.Host}{request.PathBase}{Route.Replace("{token}", token, StringComparison.Ordinal)}";
    }

    public static void MapAttachmentDownloadLinks(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(Route, DownloadAsync)
            .WithName("DownloadSignedAttachment")
            .WithSummary("Downloads one attachment through a short-lived link returned by gRPC GetAttachmentUrl.")
            .ExcludeFromDescription()
            .AllowAnonymous();

    private static async Task<IResult> DownloadAsync(string token, HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        var protection = context.RequestServices.GetRequiredService<IDataProtectionProvider>();
        if (AttachmentDownloadTarget.Parse(Unprotect(protection, token)) is not { } target)
        {
            return Results.NotFound();
        }
        var store = context.RequestServices.GetRequiredService<IAttachmentStore>();
        var content = await store.DownloadAsync(target.LayerId, target.FeatureId, target.AttachmentId, context.RequestAborted)
            .ConfigureAwait(false);
        if (content is not { } found)
        {
            return Results.NotFound();
        }
        // The stored content type is client-supplied: force a download and lock the declared MIME.
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(found.Content, found.Attachment.ContentType, found.Attachment.Filename, enableRangeProcessing: true);
    }

    private static string? Unprotect(IDataProtectionProvider protection, string token)
    {
        try
        {
            return Protector(protection).Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null; // expired, tampered or minted under a retired key
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection)
        => protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
}

internal readonly record struct AttachmentDownloadTarget(int LayerId, long FeatureId, long AttachmentId)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{LayerId}:{FeatureId}:{AttachmentId}");

    // Null for a missing, malformed or foreign token payload.
    public static AttachmentDownloadTarget? Parse(string? value)
    {
        var parts = value?.Split(':');
        return parts is { Length: 3 } &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var layerId) &&
            long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var featureId) &&
            long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var attachmentId)
            ? new AttachmentDownloadTarget(layerId, featureId, attachmentId)
            : null;
    }
}
