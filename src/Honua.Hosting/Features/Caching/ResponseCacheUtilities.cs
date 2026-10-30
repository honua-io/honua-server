// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Caching;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        // SEC-20: credentials the authentication middleware does not turn into a
        // principal (portal tokens, API keys, embed keys) must still miss the
        // shared cache. Storing them, even under a key that includes the secret,
        // would keep serving the body after revocation until the TTL elapsed.
        if (BypassOutputCacheOnCredentialedRequestPolicy.CarriesCredential(context))
        {
            return false;
        }

        if (HeaderSuppressesSharedCache(context.Request.Headers.CacheControl.ToString()) ||
            HeaderSuppressesSharedCache(context.Response.Headers.CacheControl.ToString()))
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
        var tenant = TenantScopeHelpers.ResolveRequestTenantId(request.HttpContext) ?? "<none>";
        // Same fingerprint the output cache varies on (#1983). Anonymous metadata
        // and query bodies that differ by edition must not share an entry.
        var license = ResolveLicenseKey(request);
        var testSchema = ResolveTestSchemaKey(request);
        var keyMaterial = string.Concat(
            request.Method, '|',
            request.Scheme, '|',
            request.Host.Value, '|',
            request.PathBase.Value ?? string.Empty, '|',
            request.Path.Value ?? string.Empty, '|',
            canonicalQuery, '|',
            accept, '|',
            prefer, '|',
            tenant, '|',
            license, '|',
            testSchema);

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
            parts.AddRange(entry.Value.OrderBy(v => v, StringComparer.Ordinal).Select(value =>
            {
                var valueText = value ?? string.Empty;
                return string.Concat(
                    "k",
                    entry.Key.Length.ToString(CultureInfo.InvariantCulture),
                    ":",
                    entry.Key,
                    "v",
                    valueText.Length.ToString(CultureInfo.InvariantCulture),
                    ":",
                    valueText);
            }));
        }

        return string.Join('&', parts);
    }

    private static bool HeaderSuppressesSharedCache(string header)
        => header.Contains("no-cache", StringComparison.OrdinalIgnoreCase) ||
           header.Contains("no-store", StringComparison.OrdinalIgnoreCase);

    // DefaultHttpContext throws when RequestServices was never assigned. Key
    // helpers run from unit tests that do not install a provider; those requests
    // have no license to vary on.
    private static bool TryGetRequestServices(HttpContext context, out IServiceProvider services)
    {
        try
        {
            services = context.RequestServices;
        }
        catch (InvalidOperationException)
        {
            services = null!;
            return false;
        }

        return services is not null;
    }

    private static string ResolveLicenseKey(HttpRequest request)
    {
        if (!TryGetRequestServices(request.HttpContext, out _))
        {
            return "<none>";
        }

        return OutputCachingRegistration.ResolveLicenseOutputCacheKey(request.HttpContext).Value;
    }

    private static string ResolveTestSchemaKey(HttpRequest request)
    {
        if (!TryGetRequestServices(request.HttpContext, out var services))
        {
            return string.Empty;
        }

        var configuration = services.GetService<IConfiguration>();
        if (configuration?.GetValue<bool>("HONUA_TEST_SCHEMA_HEADERS") != true)
        {
            return string.Empty;
        }

        return request.Headers["X-Honua-Test-Schema"].ToString();
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

        // Manual buffer fill (not LINQ Select) to avoid per-character allocations
        // in this hot key-normalization path.
        foreach (var normalized in (trimmed).Select(ch => char.ToLowerInvariant(ch)))
        {
            buffer[length++] = char.IsLetterOrDigit(normalized) || normalized is '-' or '_' or '.'
                ? normalized
                : '_';
        }

        return new string(buffer, 0, length);
    }
}
