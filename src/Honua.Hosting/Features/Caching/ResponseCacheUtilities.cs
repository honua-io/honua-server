// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Caching;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Caching;

internal sealed record CachedResponse(byte[] Payload, string ContentType, string? ETag = null);

internal static class ResponseCacheUtilities
{
    private const string FeatureServerPrefix = "response:query:featureserver:service:";
    private const string OgcPrefix = "response:query:ogc:collection:";
    private const string ODataPrefix = "response:query:odata:layer:";

    internal static bool ShouldCache(HttpContext context, CacheOptions options)
    {
        if (!options.Enabled || !options.ResponseCachingEnabled)
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

    internal static bool ShouldBypassAdHocSpatialResponseCache(FeatureQuery query, string? filterText = null)
        => query.SpatialFilter.HasValue ||
           SqlFilterContainsSpatialOperation(query.SqlFilter) ||
           FilterTextContainsSpatialOperation(filterText);

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
        var prefer = request.Headers["Prefer"].ToString();
        var keyMaterial = string.Concat(
            request.Method, '|',
            request.Scheme, '|',
            request.Host.Value, '|',
            request.PathBase.Value ?? string.Empty, '|',
            request.Path.Value ?? string.Empty, '|',
            canonicalQuery, '|',
            accept, '|',
            prefer);

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
                var valueText = value ?? string.Empty;
                parts.Add(string.Concat(
                    "k",
                    entry.Key.Length.ToString(CultureInfo.InvariantCulture),
                    ":",
                    entry.Key,
                    "v",
                    valueText.Length.ToString(CultureInfo.InvariantCulture),
                    ":",
                    valueText));
            }
        }

        return string.Join('&', parts);
    }

    private static bool SqlFilterContainsSpatialOperation(SqlFragment? sqlFilter)
        => sqlFilter?.Sql?.Contains("ST_", StringComparison.OrdinalIgnoreCase) == true ||
           sqlFilter?.Sql?.Contains(" && ", StringComparison.Ordinal) == true;

    private static bool FilterTextContainsSpatialOperation(string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return false;
        }

        return filterText.Contains("geo.", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("geography'", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("geometry'", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("Geometry", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("BBOX", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_INTERSECTS", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("INTERSECTS", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_CONTAINS", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_WITHIN", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_CROSSES", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_TOUCHES", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_OVERLAPS", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_DISJOINT", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_EQUALS", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_DWITHIN", StringComparison.OrdinalIgnoreCase) ||
               filterText.Contains("S_BEYOND", StringComparison.OrdinalIgnoreCase);
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
