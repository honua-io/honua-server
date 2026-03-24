// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Caching;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Infrastructure.Caching;

internal sealed record CachedResponse(byte[] Payload, string ContentType, string? ETag = null);

internal static class ResponseCacheUtilities
{
    private const string FeatureServerPrefix = "response:query:featureserver:service:";
    private const string OgcPrefix = "response:query:ogc:collection:";
    private const string ODataPrefix = "response:query:odata:layer:";

    internal static bool ShouldCache(HttpContext context, CacheOptions options)
    {
        if (!options.Enabled)
        {
            return false;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        if (context.User?.Identity?.IsAuthenticated == true)
        {
            return false;
        }

        var cacheControl = context.Request.Headers.CacheControl.ToString();
        if (cacheControl.Contains("no-cache", StringComparison.OrdinalIgnoreCase) ||
            cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pragma = context.Request.Headers.Pragma.ToString();
        if (pragma.Contains("no-cache", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    internal static string BuildFeatureServerKey(string serviceId, int layerId, HttpRequest request)
        => BuildKey($"{FeatureServerPrefix}{NormalizeKeyPart(serviceId)}:layer:{layerId}:", request);

    internal static string BuildFeatureServerLayerPattern(int layerId)
        => $"{FeatureServerPrefix}*:layer:{layerId}:*";

    internal static string BuildFeatureServerServicePattern(string serviceId)
        => $"{FeatureServerPrefix}{NormalizeKeyPart(serviceId)}:*";

    internal static string BuildFeatureServerLayerPattern(string serviceId, int layerId)
        => $"{FeatureServerPrefix}{NormalizeKeyPart(serviceId)}:layer:{layerId}:*";

    internal static string BuildOgcCollectionKey(string collectionId, HttpRequest request)
        => BuildKey($"{OgcPrefix}{NormalizeKeyPart(collectionId)}:", request);

    internal static string BuildOgcCollectionPattern(string collectionId)
        => $"{OgcPrefix}{NormalizeKeyPart(collectionId)}:*";

    internal static string BuildODataLayerKey(int layerId, HttpRequest request)
        => BuildKey($"{ODataPrefix}{layerId}:", request);

    internal static string BuildODataLayerPattern(int layerId)
        => $"{ODataPrefix}{layerId}:*";

    internal static string BuildFeatureServerPattern()
        => $"{FeatureServerPrefix}*";

    internal static string BuildOgcPattern()
        => $"{OgcPrefix}*";

    internal static string BuildODataPattern()
        => $"{ODataPrefix}*";

    internal static CachedResponse CreateCachedResponse(byte[] payload, string contentType, IETagService etagService)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(etagService);

        var etag = etagService.ComputeETag(payload);
        return new CachedResponse(payload, contentType, etag);
    }

    internal static IResult CreateResultFromCachedResponse(
        HttpContext context,
        CachedResponse cached,
        IETagService etagService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cached);
        ArgumentNullException.ThrowIfNull(etagService);

        if (!string.IsNullOrWhiteSpace(cached.ETag))
        {
            var ifMatch = context.Request.Headers.IfMatch.ToString();
            if (!string.IsNullOrEmpty(ifMatch) && !etagService.MatchesPrecondition(ifMatch, cached.ETag))
            {
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
            }

            var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
            if (!etagService.IsModified(ifNoneMatch, cached.ETag))
            {
                etagService.SetCacheHeaders(context.Response, cached.ETag);
                context.Response.Headers.Remove("Content-Type");
                context.Response.ContentLength = 0;
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            etagService.SetCacheHeaders(context.Response, cached.ETag);
        }

        return Results.Bytes(cached.Payload, cached.ContentType);
    }

    private static string BuildKey(string prefix, HttpRequest request)
    {
        var canonicalQuery = BuildCanonicalQueryString(request.Query);
        var accept = request.Headers.Accept.ToString();
        var keyMaterial = string.Concat(
            request.Method, '|',
            request.Path.Value ?? string.Empty, '|',
            canonicalQuery, '|',
            accept);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)))
            .ToLowerInvariant();

        return string.Concat(prefix, hash);
    }

    private static string BuildCanonicalQueryString(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(query.Count);
        foreach (var entry in query.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var value in entry.Value.OrderBy(v => v, StringComparer.Ordinal))
            {
                parts.Add(string.Concat(entry.Key, '=', value));
            }
        }

        return string.Join('&', parts);
    }

    private static string NormalizeKeyPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var trimmed = value.Trim();
        var buffer = new char[trimmed.Length];
        var length = 0;

        foreach (var ch in trimmed)
        {
            var normalized = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(normalized) || normalized is '-' or '_' or '.')
            {
                buffer[length++] = normalized;
            }
            else
            {
                buffer[length++] = '_';
            }
        }

        return new string(buffer, 0, length);
    }
}
