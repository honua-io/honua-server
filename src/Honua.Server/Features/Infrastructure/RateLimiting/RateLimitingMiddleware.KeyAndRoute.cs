// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Frozen;
using System.Security.Claims;
using System.Text;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Routing;

namespace Honua.Infrastructure.RateLimiting;

internal sealed partial class RateLimitingMiddleware
{
    private enum RouteLimitCategory
    {
        None,
        Upload,
        Metadata,
        Query
    }

    private readonly struct AuthenticatedSubjectKeyParts
    {
        public string? TenantId { get; init; }
        public required string AuthenticationScheme { get; init; }
        public required string IssuerScope { get; init; }
        public required string AuthenticatedSubject { get; init; }
    }

    private static readonly SearchValues<string> UploadPathTokens = SearchValues.Create(
        ["/upload", "/import/", "/attachments/add", "/ingest"],
        StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> CollectionNonMetadataTokens = SearchValues.Create(
        ["/items", "/tiles", "/coverage"],
        StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> RestServicesNonMetadataTokens = SearchValues.Create(
        ["/applyEdits", "/tile/"],
        StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> QueryPathContainsTokens = SearchValues.Create(
        ["/query/", "/items/"],
        StringComparison.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> QueryTerminalSegments = new[]
    {
        "query", "items", "search", "find", "identify", "export"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static string FormatAuthenticatedSubjectKey(AuthenticatedSubjectKeyParts parts)
    {
        var schemeByteCount = Encoding.UTF8.GetByteCount(parts.AuthenticationScheme);
        var issuerByteCount = Encoding.UTF8.GetByteCount(parts.IssuerScope);
        var subjectByteCount = Encoding.UTF8.GetByteCount(parts.AuthenticatedSubject);
        var maxUtf8Bytes = Math.Max(schemeByteCount, Math.Max(issuerByteCount, subjectByteCount));
        var encodedLength = Base64CharLength(schemeByteCount)
            + Base64CharLength(issuerByteCount)
            + Base64CharLength(subjectByteCount);
        var totalChars = ComputeSubjectKeyLength(parts.TenantId, encodedLength);

        byte[]? rentedBytes = null;
        char[]? rentedChars = null;
        try
        {
            Span<byte> utf8Scratch = maxUtf8Bytes <= 256
                ? stackalloc byte[256]
                : (rentedBytes = ArrayPool<byte>.Shared.Rent(maxUtf8Bytes));
            Span<char> chars = totalChars <= 512
                ? stackalloc char[512]
                : (rentedChars = ArrayPool<char>.Shared.Rent(totalChars));

            var written = WriteAuthenticatedSubjectKey(parts, utf8Scratch, chars);
            return new string(chars[..written]);
        }
        finally
        {
            if (rentedBytes is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedBytes);
            }

            if (rentedChars is not null)
            {
                ArrayPool<char>.Shared.Return(rentedChars);
            }
        }
    }

    private static int ComputeSubjectKeyLength(string? tenantId, int encodedComponentChars)
        => (!string.IsNullOrEmpty(tenantId) ? TenantKeyFamily.Length + 1 + tenantId.Length + 1 : 0)
            + UserKeyFamily.Length + 1
            + encodedComponentChars + 2;

    private static int Base64CharLength(int byteCount) => ((byteCount + 2) / 3) * 4;

    private static int WriteAuthenticatedSubjectKey(
        AuthenticatedSubjectKeyParts parts,
        Span<byte> utf8Scratch,
        Span<char> chars)
    {
        var pos = 0;
        var tenantSpan = parts.TenantId.AsSpan();
        if (!tenantSpan.IsEmpty)
        {
            TenantKeyFamily.AsSpan().CopyTo(chars[pos..]);
            pos += TenantKeyFamily.Length;
            chars[pos++] = ':';
            tenantSpan.CopyTo(chars[pos..]);
            pos += tenantSpan.Length;
            chars[pos++] = '|';
        }

        UserKeyFamily.AsSpan().CopyTo(chars[pos..]);
        pos += UserKeyFamily.Length;
        chars[pos++] = ':';

        pos += WriteBase64Component(parts.AuthenticationScheme.AsSpan(), utf8Scratch, chars[pos..]);
        chars[pos++] = ':';
        pos += WriteBase64Component(parts.IssuerScope.AsSpan(), utf8Scratch, chars[pos..]);
        chars[pos++] = ':';
        pos += WriteBase64Component(parts.AuthenticatedSubject.AsSpan(), utf8Scratch, chars[pos..]);
        return pos;
    }

    private static int WriteBase64Component(
        ReadOnlySpan<char> value,
        Span<byte> utf8Scratch,
        Span<char> destination)
    {
        var bytesWritten = Encoding.UTF8.GetBytes(value, utf8Scratch);
        if (!Convert.TryToBase64Chars(utf8Scratch[..bytesWritten], destination, out var charsWritten))
        {
            throw new InvalidOperationException("Destination buffer too small for Base64 rate-limit key component.");
        }

        return charsWritten;
    }

    private static ResolvedRateLimit? ResolveExplicitEndpointLimit(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var endpointRateLimit = endpoint?.Metadata.GetMetadata<RateLimitAttribute>();
        if (endpointRateLimit is null)
        {
            return null;
        }

        return new ResolvedRateLimit(
            endpointRateLimit.RequestsPerMinute,
            TimeSpan.FromMinutes(1),
            GetEndpointScope(endpoint, context));
    }

    private ResolvedRateLimit? ResolveEndpointOrCategoryLimit(HttpContext context)
        => ResolveExplicitEndpointLimit(context) ?? ResolveRouteCategoryLimit(context);

    private ResolvedRateLimit? ResolveRouteCategoryLimit(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return ClassifyRouteCategory(context, path.AsSpan()) switch
        {
            RouteLimitCategory.Upload => new ResolvedRateLimit(_options.UploadRequestsPerMinute, TimeSpan.FromMinutes(1), "category:upload"),
            RouteLimitCategory.Metadata => new ResolvedRateLimit(_options.MetadataRequestsPerMinute, TimeSpan.FromMinutes(1), "category:metadata"),
            RouteLimitCategory.Query => new ResolvedRateLimit(_options.QueryRequestsPerMinute, TimeSpan.FromMinutes(1), "category:query"),
            _ => null
        };
    }

    private static RouteLimitCategory ClassifyRouteCategory(HttpContext context, ReadOnlySpan<char> path)
    {
        var method = context.Request.Method;
        if ((HttpMethods.IsPost(method) || HttpMethods.IsPut(method)) && path.ContainsAny(UploadPathTokens))
        {
            return RouteLimitCategory.Upload;
        }

        var isReadVerb = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (isReadVerb && IsExplicitMetadataRequest(context, path))
        {
            return RouteLimitCategory.Metadata;
        }

        if (isReadVerb && path.StartsWith("/rest/services", StringComparison.OrdinalIgnoreCase))
        {
            if (IsQueryRequest(path))
            {
                return RouteLimitCategory.Query;
            }

            return path.ContainsAny(RestServicesNonMetadataTokens)
                ? RouteLimitCategory.None
                : RouteLimitCategory.Metadata;
        }

        return IsQueryRequest(path) ? RouteLimitCategory.Query : RouteLimitCategory.None;
    }

    private static bool IsExplicitMetadataRequest(HttpContext context, ReadOnlySpan<char> path)
    {
        if (path.Contains("/openapi", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/conformance", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/docs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (context.Request.Query.TryGetValue("request", out var ogcRequest)
            && ogcRequest.Count == 1
            && string.Equals(ogcRequest[0], "GetCapabilities", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Contains("/collections", StringComparison.OrdinalIgnoreCase)
            && !path.ContainsAny(CollectionNonMetadataTokens);
    }

    private static bool IsQueryRequest(ReadOnlySpan<char> path)
    {
        if (path.StartsWith("/odata", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/wfs", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/wms", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/wcs", StringComparison.OrdinalIgnoreCase)
            || path.ContainsAny(QueryPathContainsTokens))
        {
            return true;
        }

        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0
            && QueryTerminalSegments.GetAlternateLookup<ReadOnlySpan<char>>().Contains(path[(lastSlash + 1)..]);
    }

    private static string GetEndpointScope(Endpoint? endpoint, HttpContext context)
    {
        var methods = endpoint?.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var method = methods is { Count: > 0 }
            ? string.Join(",", methods)
            : context.Request.Method;

        if (endpoint is RouteEndpoint routeEndpoint)
        {
            var routeTemplate = routeEndpoint.RoutePattern.RawText;
            if (!string.IsNullOrEmpty(routeTemplate))
            {
                return $"{method} {routeTemplate}";
            }
        }

        var target = string.IsNullOrEmpty(endpoint?.DisplayName) ? "endpoint" : endpoint.DisplayName;
        return $"{method} {target}";
    }

    private static RateLimitRequestDescriptor BuildRequestDescriptor(HttpContext context)
    {
        var apiKeyIdentity = context.User?.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && string.Equals(identity.AuthenticationType, AuthenticationExtensions.ApiKeyScheme, StringComparison.Ordinal));
        var apiKeyId = apiKeyIdentity?.FindFirst("api_key_id")?.Value;
        var apiKey = string.IsNullOrEmpty(apiKeyId) ? null : apiKeyId;

        var tenantContext = context.RequestServices?.GetService(typeof(ITenantContext)) as ITenantContext;
        var tenantId = string.IsNullOrEmpty(tenantContext?.TenantId) ? null : tenantContext.TenantId;

        var plan = (context.User as ClaimsPrincipal)?.FindFirst("plan")?.Value
            ?? (context.User as ClaimsPrincipal)?.FindFirst("honua_plan")?.Value;
        if (string.IsNullOrEmpty(plan))
        {
            plan = null;
        }

        return new RateLimitRequestDescriptor
        {
            ApiKey = apiKey,
            TenantId = tenantId,
            Plan = plan,
        };
    }

    private static (string Family, string Hash) SplitRateLimitKey(string? rateLimitKey)
    {
        if (string.IsNullOrEmpty(rateLimitKey))
        {
            return (UnknownKeyFamily, LogValueRedactor.Hash(null));
        }

        var lastSegment = rateLimitKey;
        var pipeIndex = rateLimitKey.LastIndexOf('|');
        if (pipeIndex >= 0 && pipeIndex < rateLimitKey.Length - 1)
        {
            lastSegment = rateLimitKey[(pipeIndex + 1)..];
        }

        var separatorIndex = lastSegment.IndexOf(':', StringComparison.Ordinal);
        var family = separatorIndex <= 0 ? UnknownKeyFamily : lastSegment[..separatorIndex];

        var resolvedFamily = family switch
        {
            TenantKeyFamily => TenantKeyFamily,
            UserKeyFamily => UserKeyFamily,
            IpKeyFamily => IpKeyFamily,
            _ => UnknownKeyFamily
        };

        return (resolvedFamily, LogValueRedactor.Hash(rateLimitKey));
    }
}
