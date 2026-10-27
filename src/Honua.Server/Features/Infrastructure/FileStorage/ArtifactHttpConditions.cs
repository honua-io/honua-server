// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Infrastructure.FileStorage;

/// <summary>
/// HTTP preconditions for an already resolved and authorized artifact representation.
/// Storage reads must separately pin the metadata revision used by this policy.
/// </summary>
internal static class ArtifactHttpConditions
{
    internal static ArtifactHttpDecision Evaluate(HttpContext context, CloudFile metadata)
    {
        var etag = CreateEntityTag(metadata);
        var lastModified = DateTimeOffset.FromUnixTimeSeconds(metadata.UploadedAt.ToUnixTimeSeconds());
        context.Response.Headers[HeaderNames.ETag] = etag.ToString();
        context.Response.Headers[HeaderNames.LastModified] = lastModified.ToString("R", CultureInfo.InvariantCulture);
        context.Response.Headers[HeaderNames.AcceptRanges] = "bytes";

        var headers = context.Request.Headers;
        // RFC 9110 section 13.2.2: entity-tag preconditions supersede their date counterpart.
        if (headers.ContainsKey(HeaderNames.IfMatch))
        {
            if (TryParseTags(headers[HeaderNames.IfMatch].ToString(), out var tags) &&
                !tags.Any(candidate => candidate.Tag == "*" || candidate.Compare(etag, useStrongComparison: true)))
            {
                return new(StatusCodes.Status412PreconditionFailed, null);
            }
        }
        else if (TryReadDate(headers[HeaderNames.IfUnmodifiedSince].ToString(), out var unmodified) && lastModified > unmodified)
        {
            return new(StatusCodes.Status412PreconditionFailed, null);
        }

        if (headers.ContainsKey(HeaderNames.IfNoneMatch))
        {
            if (TryParseTags(headers[HeaderNames.IfNoneMatch].ToString(), out var tags) &&
                tags.Any(candidate => candidate.Tag == "*" || candidate.Compare(etag, useStrongComparison: false)))
            {
                return new(StatusCodes.Status304NotModified, null);
            }
        }
        else if (TryReadDate(headers[HeaderNames.IfModifiedSince].ToString(), out var modified) && lastModified <= modified)
        {
            return new(StatusCodes.Status304NotModified, null);
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return new(null, null);
        }

        var range = headers[HeaderNames.Range].ToString();
        if (!string.IsNullOrWhiteSpace(range) && headers.ContainsKey(HeaderNames.IfRange))
        {
            // Last-Modified cannot be proven strong: mutable objects may change twice within
            // its one-second precision. Date/weak/invalid If-Range therefore requires a full
            // response. A strong matching entity tag is the only safe range resume validator.
            if (!EntityTagHeaderValue.TryParse(headers[HeaderNames.IfRange].ToString(), out var candidate) ||
                candidate.Tag == "*" || !candidate.Compare(etag, useStrongComparison: true))
            {
                range = null;
            }
        }

        return new(null, range);
    }

    internal static EntityTagHeaderValue CreateEntityTag(CloudFile metadata)
    {
        var contentHash = metadata.ContentHash;
        var hasContentHash = contentHash is { Length: 64 } && contentHash.All(Uri.IsHexDigit);
        var revision = metadata.ETag;
        var hasRevision = !string.IsNullOrWhiteSpace(revision) &&
            !revision.StartsWith("W/", StringComparison.OrdinalIgnoreCase) &&
            !revision.StartsWith("legacy-", StringComparison.Ordinal);
        var identity = hasContentHash
            ? "sha256:" + contentHash!.ToUpperInvariant()
            : hasRevision
                ? metadata.Provider + ":" + revision
                : string.Create(CultureInfo.InvariantCulture, $"unversioned:{metadata.SizeBytes}:{metadata.UploadedAt.UtcTicks}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new EntityTagHeaderValue($"\"{hash}\"", isWeak: !hasContentHash && !hasRevision);
    }

    private static bool TryParseTags(string value, out IList<EntityTagHeaderValue> tags)
        => EntityTagHeaderValue.TryParseList([value], out tags!);

    private static bool TryReadDate(string value, out DateTimeOffset result)
        => HeaderUtilities.TryParseDate(value, out result);
}

internal readonly record struct ArtifactHttpDecision(int? StatusCode, string? RangeHeader);
