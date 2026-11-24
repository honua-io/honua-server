// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Server.Features.Operations;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.RateLimiting;

// A server-only request feature; never reconstructed from caller headers.
internal sealed class RateLimitAdmission
{
}

// The request is allowed only when both the subject counter and the endpoint counter are under their limits.
internal sealed partial class RateLimitingMiddleware
{
    private const string TenantKeyFamily = "tenant";
    private const string UserKeyFamily = "user";
    private const string IpKeyFamily = "ip";
    private const string UnknownKeyFamily = "unknown";

    // At capacity the least-recently-used entry in the shard is evicted in O(1) so a flood of new keys
    // cannot deny unrelated subjects or stall unrelated shards.
    internal const int MaxMemoryCounterEntries = 10_000;
    private const int CounterShardCount = 16;

    private static readonly RateLimitAdmission Admitted = new();
    private static readonly long MemoryCapacityLogIntervalTicks = TimeSpan.TicksPerMinute;
    private static readonly ShardedRateLimitCounterStore SharedCounterStore = new(MaxMemoryCounterEntries, CounterShardCount);
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

    private readonly RequestDelegate _next;
    private readonly IRateLimitPolicyStore _policyStore;
    private readonly RateLimitingOptions _options;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ShardedRateLimitCounterStore _counterStore;
    private long _lastMemoryCapacityLogTicks;

    public RateLimitingMiddleware(
        RequestDelegate next,
        IRateLimitPolicyStore policyStore,
        IOptions<RateLimitingOptions> options,
        ILogger<RateLimitingMiddleware> logger,
        TimeProvider? timeProvider = null,
        ShardedRateLimitCounterStore? counterStore = null)
    {
        _next = next;
        _policyStore = policyStore;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _counterStore = counterStore ?? SharedCounterStore;
    }

    internal static int SharedMemoryCounterCount => SharedCounterStore.Count;

    internal static void ClearSharedMemoryCounters() => SharedCounterStore.Clear();

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // Only the one-use process-local attestation can install this feature.
        // The original admitted request already paid for its exact URL-import execution.
        var admittedLoopback = context.Features.Get<OperationLoopbackAdmission>();
        context.Features.Set<OperationLoopbackAdmission>(null);
        if (admittedLoopback is not null &&
            string.Equals(admittedLoopback.TenantId ?? string.Empty,
                context.RequestServices.GetService<ITenantContext>()?.TenantId ?? string.Empty,
                StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        // Determine rate limit key from request
        var rateLimitKey = DetermineRateLimitKey(context);
        if (string.IsNullOrEmpty(rateLimitKey))
        {
            await _next(context);
            return;
        }

        // Check if request exceeds rate limit
        var rateLimitResult = await CheckRateLimitAsync(rateLimitKey, context);

        if (rateLimitResult.IsAllowed)
        {
            context.Features.Set(Admitted);
            // Add rate limit headers
            AddRateLimitHeaders(context, rateLimitResult);
            await _next(context);
        }
        else
        {
            // Request blocked due to rate limit
            await HandleRateLimitExceededAsync(context, rateLimitResult);
        }
    }

    private static string? DetermineRateLimitKey(HttpContext context)
    {
        // Skip rate limiting for health probes and metrics endpoints.
        var path = context.Request.Path;
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/metrics", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/ready", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Tenant scope (resolved by TenantContextMiddleware, which runs earlier in the
        // pipeline). When present it prefixes the bucket so two tenants sharing an
        // identity provider — or an anonymous IP straddling tenants — never share a
        // counter. The id is a stable opaque tenant identifier, never a secret.
        var tenantId = context.RequestServices?.GetService(typeof(ITenantContext)) is ITenantContext tenantContext
            && !string.IsNullOrEmpty(tenantContext.TenantId)
            ? tenantContext.TenantId
            : null;

        // Key on the authenticated principal first (even if RemoteIpAddress is absent, e.g. Unix
        // domain socket behind a reverse proxy). Never derive the bucket from raw, unauthenticated
        // credentials (Authorization header or ?api_key=).
        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            // Prefer the stable subject identifiers carried by OIDC/JWT principals. Many
            // providers intentionally omit a display-name claim, and falling those callers
            // back to the source IP would make unrelated users behind one NAT share a bucket.
            // Keep the API-key id/name fallbacks aligned with StudioAuthorizationService's
            // actor resolution before finally using ClaimsIdentity.Name for legacy schemes.
            var authenticatedSubject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(authenticatedSubject))
            {
                authenticatedSubject = principal.FindFirst("sub")?.Value;
            }

            if (string.IsNullOrWhiteSpace(authenticatedSubject))
            {
                authenticatedSubject = principal.FindFirst("api_key_id")?.Value;
            }

            if (string.IsNullOrWhiteSpace(authenticatedSubject))
            {
                authenticatedSubject = principal.FindFirst("api_key_name")?.Value;
            }

            if (string.IsNullOrWhiteSpace(authenticatedSubject))
            {
                authenticatedSubject = principal.Identity.Name;
            }

            if (!string.IsNullOrWhiteSpace(authenticatedSubject))
            {
                var authenticationScheme = string.IsNullOrWhiteSpace(principal.Identity.AuthenticationType)
                    ? "authenticated"
                    : principal.Identity.AuthenticationType;
                var issuer = principal.FindFirst("iss")?.Value;
                var issuerScope = string.IsNullOrWhiteSpace(issuer) ? "no-issuer" : issuer;
                // Encode every tuple component independently before adding separators. Raw
                // issuer and subject strings may themselves contain ':', so concatenating them
                // directly would let distinct validated identities collide on one counter.
                return FormatAuthenticatedSubjectKey(tenantId, authenticationScheme, issuerScope, authenticatedSubject);
            }
        }

        // Fall back to IP-based rate limiting for unauthenticated requests (and a bounded
        // unknown bucket if no client IP is available, rather than bypassing rate limiting).
        var clientIp = GetClientIpAddress(context);
        if (string.IsNullOrEmpty(tenantId))
        {
            return string.IsNullOrEmpty(clientIp)
                ? $"{IpKeyFamily}:{UnknownKeyFamily}"
                : $"{IpKeyFamily}:{clientIp}";
        }

        return string.IsNullOrEmpty(clientIp)
            ? $"{TenantKeyFamily}:{tenantId}|{IpKeyFamily}:{UnknownKeyFamily}"
            : $"{TenantKeyFamily}:{tenantId}|{IpKeyFamily}:{clientIp}";
    }

    private static string FormatAuthenticatedSubjectKey(
        string? tenantId,
        string authenticationScheme,
        string issuerScope,
        string authenticatedSubject)
    {
        var tenantSpan = tenantId.AsSpan();
        var schemeByteCount = Encoding.UTF8.GetByteCount(authenticationScheme);
        var issuerByteCount = Encoding.UTF8.GetByteCount(issuerScope);
        var subjectByteCount = Encoding.UTF8.GetByteCount(authenticatedSubject);

        var maxUtf8Bytes = Math.Max(schemeByteCount, Math.Max(issuerByteCount, subjectByteCount));
        var schemeBase64Length = ((schemeByteCount + 2) / 3) * 4;
        var issuerBase64Length = ((issuerByteCount + 2) / 3) * 4;
        var subjectBase64Length = ((subjectByteCount + 2) / 3) * 4;

        var totalChars = (!tenantSpan.IsEmpty ? TenantKeyFamily.Length + 1 + tenantSpan.Length + 1 : 0)
            + UserKeyFamily.Length + 1
            + schemeBase64Length + 1
            + issuerBase64Length + 1
            + subjectBase64Length;

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

            var pos = 0;
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

            pos += WriteBase64Component(authenticationScheme.AsSpan(), utf8Scratch, chars[pos..]);
            chars[pos++] = ':';
            pos += WriteBase64Component(issuerScope.AsSpan(), utf8Scratch, chars[pos..]);
            chars[pos++] = ':';
            pos += WriteBase64Component(authenticatedSubject.AsSpan(), utf8Scratch, chars[pos..]);

            return new string(chars[..pos]);
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

    private async Task<RateLimitResult> CheckRateLimitAsync(string rateLimitKey, HttpContext context)
    {
        try
        {
            // Always meter the subject's shared tier/global counter: the ceiling that bounds total
            // traffic for the subject across every endpoint. Tier resolution (api-key > tenant >
            // plan, else global default) feeds the same unscoped counter.
            var (subjectLimit, fromPolicyStore) = await ResolveSubjectLimitAsync(context);
            var subjectResult = CheckRateLimitMemory(rateLimitKey.AsSpan(), subjectLimit);

            // When an endpoint declares an explicit per-endpoint limit, or matches a configured
            // route category ceiling (when not overridden by an explicit tier policy), additionally
            // meter a scoped counter so high-volume traffic on one endpoint/category cannot drain
            // another endpoint's allowance for the same subject.
            var endpointLimit = ResolveEndpointLimit(context, includeCategoryDefaults: !fromPolicyStore);
            if (endpointLimit is null)
            {
                return subjectResult;
            }

            var endpointResult = CheckScopedRateLimitMemory(rateLimitKey, endpointLimit.Value);

            return CombineResults(subjectResult, endpointResult);
        }
        // Intentional catch-all: this is the request-handling boundary for the rate-limit
        // check (policy-store faults, etc.); allow the request through on failure rather
        // than blocking legitimate traffic.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var (keyFamily, keyHash) = SplitRateLimitKey(rateLimitKey);
            RateLimitingLog.RateLimitCheckFailed(_logger, keyFamily, keyHash, ex);

            return new RateLimitResult
            {
                IsAllowed = true,
                RequestsRemaining = _options.GlobalRequestsPerMinute,
                WindowReset = _timeProvider.GetUtcNow().AddMinutes(1)
            };
        }
    }

    private static RateLimitResult CombineResults(RateLimitResult subject, RateLimitResult endpoint)
    {
        if (subject.IsAllowed && endpoint.IsAllowed)
        {
            return endpoint.RequestsRemaining <= subject.RequestsRemaining ? endpoint : subject;
        }

        // At least one counter tripped. Advise the longest wait among the counters that failed.
        if (!subject.IsAllowed && !endpoint.IsAllowed)
        {
            return subject.WindowReset >= endpoint.WindowReset ? subject : endpoint;
        }

        return subject.IsAllowed ? endpoint : subject;
    }

    private RateLimitResult CheckScopedRateLimitMemory(string rateLimitKey, ResolvedRateLimit endpointLimit)
    {
        const string policySeparator = "|policy:";
        var scope = endpointLimit.Scope;
        var totalLength = rateLimitKey.Length + policySeparator.Length + scope.Length;
        char[]? rented = null;
        try
        {
            Span<char> buffer = totalLength <= 512
                ? stackalloc char[512]
                : (rented = ArrayPool<char>.Shared.Rent(totalLength));

            rateLimitKey.AsSpan().CopyTo(buffer);
            policySeparator.AsSpan().CopyTo(buffer[rateLimitKey.Length..]);
            scope.AsSpan().CopyTo(buffer[(rateLimitKey.Length + policySeparator.Length)..]);

            return CheckRateLimitMemory(buffer[..totalLength], endpointLimit);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private RateLimitResult CheckRateLimitMemory(ReadOnlySpan<char> counterKey, ResolvedRateLimit resolved)
    {
        var now = _timeProvider.GetUtcNow();
        var result = _counterStore.RecordHit(counterKey, resolved, now, out var evicted);
        if (evicted)
        {
            LogMemoryCounterEviction(now);
        }

        return result;
    }

    private void LogMemoryCounterEviction(DateTimeOffset now)
    {
        var lastTicks = Volatile.Read(ref _lastMemoryCapacityLogTicks);
        if (now.UtcTicks - lastTicks < MemoryCapacityLogIntervalTicks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastMemoryCapacityLogTicks, now.UtcTicks, lastTicks) == lastTicks)
        {
            RateLimitingLog.MemoryCounterCapacityEvicted(_logger, _counterStore.Capacity);
        }
    }

    private ResolvedRateLimit? ResolveEndpointLimit(HttpContext context, bool includeCategoryDefaults)
    {
        var endpoint = context.GetEndpoint();
        var endpointRateLimit = endpoint?.Metadata.GetMetadata<RateLimitAttribute>();
        if (endpointRateLimit is not null)
        {
            return new ResolvedRateLimit(
                endpointRateLimit.RequestsPerMinute,
                TimeSpan.FromMinutes(1),
                GetEndpointScope(endpoint, context));
        }

        return includeCategoryDefaults ? ResolveRouteCategoryLimit(context) : null;
    }

    private ResolvedRateLimit? ResolveRouteCategoryLimit(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var pathSpan = path.AsSpan();
        var method = context.Request.Method;
        if ((HttpMethods.IsPost(method) || HttpMethods.IsPut(method))
            && pathSpan.ContainsAny(UploadPathTokens))
        {
            return new ResolvedRateLimit(_options.UploadRequestsPerMinute, TimeSpan.FromMinutes(1), "category:upload");
        }

        bool? isQuery = null;
        if (IsMetadataRequest(context, pathSpan, method, ref isQuery))
        {
            return new ResolvedRateLimit(_options.MetadataRequestsPerMinute, TimeSpan.FromMinutes(1), "category:metadata");
        }

        if (isQuery ?? IsQueryRequest(pathSpan))
        {
            return new ResolvedRateLimit(_options.QueryRequestsPerMinute, TimeSpan.FromMinutes(1), "category:query");
        }

        return null;
    }

    private static bool IsMetadataRequest(HttpContext context, ReadOnlySpan<char> path, string method, ref bool? isQuery)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return false;
        }

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

        if (path.Contains("/collections", StringComparison.OrdinalIgnoreCase)
            && !path.ContainsAny(CollectionNonMetadataTokens))
        {
            return true;
        }

        if (!path.StartsWith("/rest/services", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        isQuery = IsQueryRequest(path);
        return !isQuery.Value && !path.ContainsAny(RestServicesNonMetadataTokens);
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

    private async Task<(ResolvedRateLimit Limit, bool FromPolicyStore)> ResolveSubjectLimitAsync(HttpContext context)
    {
        // Subject-tier policies (per-plan / per-tenant / per-api-key) drive the ceiling when one
        // matches. Resolution order is centralized in RateLimitPolicyResolver so the precedence is
        // identical everywhere it is applied. Tier limits meter the subject across all endpoints, so
        // they carry no endpoint scope.
        IReadOnlyList<RateLimitPolicy>? policies;
        try
        {
            policies = await _policyStore.ListPoliciesAsync(context.RequestAborted);
        }
        // A policy store outage must not disable limiting: fall back to the global default below.
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            RateLimitingLog.PolicyStoreUnavailable(_logger, ex);
            policies = null;
        }

        if (policies is { Count: > 0 })
        {
            var descriptor = BuildRequestDescriptor(context);
            var policy = RateLimitPolicyResolver.Resolve(policies, descriptor);
            if (policy is not null && policy.RequestsPerWindow > 0 && policy.WindowDuration > TimeSpan.Zero)
            {
                return (new ResolvedRateLimit(policy.RequestsPerWindow, policy.WindowDuration, string.Empty), true);
            }
        }

        // Global default (one-minute window), shared across endpoints for the subject.
        return (new ResolvedRateLimit(_options.GlobalRequestsPerMinute, TimeSpan.FromMinutes(1), string.Empty), false);
    }

    private static string GetEndpointScope(Endpoint? endpoint, HttpContext context)
    {
        // Prefer the routed method metadata (may enumerate several verbs); fall back to the actual
        // request verb so a non-routed endpoint still partitions per method.
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

    // The tier descriptor uses opaque identifiers, never raw secrets.
    private static RateLimitRequestDescriptor BuildRequestDescriptor(HttpContext context)
    {
        // Only an API-key principal carries an api-key tier, and the tier key is the stable
        // api_key_id. Identity.Name is a display name: an OIDC user (or a second key) with the
        // same name must not inherit another key's quota.
        var apiKeyIdentity = context.User?.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && string.Equals(identity.AuthenticationType, AuthenticationExtensions.ApiKeyScheme, StringComparison.Ordinal));
        var apiKeyId = apiKeyIdentity?.FindFirst("api_key_id")?.Value;
        var apiKey = string.IsNullOrEmpty(apiKeyId) ? null : apiKeyId;

        var tenantContext = context.RequestServices?.GetService(typeof(ITenantContext)) as ITenantContext;
        var tenantId = string.IsNullOrEmpty(tenantContext?.TenantId) ? null : tenantContext.TenantId;

        // The billing plan is carried as a claim when present (added by the auth provider or a
        // downstream enrichment step); absent for anonymous traffic.
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

    private static void AddRateLimitHeaders(HttpContext context, RateLimitResult result)
    {
        context.Response.Headers.TryAdd("X-RateLimit-Limit", result.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        context.Response.Headers.TryAdd("X-RateLimit-Remaining", result.RequestsRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        context.Response.Headers.TryAdd("X-RateLimit-Reset", result.WindowReset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task HandleRateLimitExceededAsync(HttpContext context, RateLimitResult result)
    {
        var (keyFamily, keyHash) = SplitRateLimitKey(DetermineRateLimitKey(context));
        RateLimitingLog.RateLimitExceeded(_logger, keyFamily, keyHash, result.RequestCount, result.Limit);

        AddRateLimitHeaders(context, result);

        // RFC 9110 Retry-After: advise clients how long to wait (in whole seconds, never
        // negative) before retrying. Mirrors the X-RateLimit-Reset window boundary.
        var retryAfterSeconds = Math.Max(0, (int)Math.Ceiling((result.WindowReset - _timeProvider.GetUtcNow()).TotalSeconds));
        if (_options.StatusCode != StatusCodes.Status429TooManyRequests && !IsProtocolFramedBackpressure(context))
        {
            context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["Honua-Retryable"] = "true";
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    _options.StatusCode,
                    ProblemDetailsHelpers.GetTitle(_options.StatusCode),
                    "Too many requests. Please try again later.")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        await BackpressureResponseWriter.WriteAsync(
            context,
            BackpressureKind.Throttled,
            retryAfterSeconds,
            context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsProtocolFramedBackpressure(HttpContext context)
    {
        var path = context.Request.Path;
        if (HttpMethods.IsPost(context.Request.Method)
            && (path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/mcp/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true
            || path.StartsWithSegments("/honua.v1", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/geospatial.v1", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetClientIpAddress(HttpContext context)
    {
        // Trust the connection IP only. If forwarded headers are enabled, the
        // ASP.NET Core forwarded-headers middleware will already have rewritten
        // RemoteIpAddress after validating the proxy against the known-proxy list.
        // Anonymous keys use this string; authenticated keys use the subject and never this value.
        return ClientAddressPartition.From(context.Connection.RemoteIpAddress);
    }

    // Logs report a family label and a hash, so the tenant, principal, and address do not appear.
    private static (string Family, string Hash) SplitRateLimitKey(string? rateLimitKey)
    {
        if (string.IsNullOrEmpty(rateLimitKey))
        {
            return (UnknownKeyFamily, LogValueRedactor.Hash(null));
        }

        // The bucket family is identified by the last segment so a tenant-prefixed key
        // reports user/ip rather than the tenant scope marker.
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

        // Hash the full key (including tenant prefix) so distinct buckets get distinct
        // correlation hashes without exposing any identifier.
        return (resolvedFamily, LogValueRedactor.Hash(rateLimitKey));
    }
}

internal readonly record struct ResolvedRateLimit(int Limit, TimeSpan Window, string Scope);

internal readonly record struct RateLimitResult
{
    public required bool IsAllowed { get; init; }

    public required int RequestsRemaining { get; init; }

    public required DateTimeOffset WindowReset { get; init; }

    public int RequestCount { get; init; }

    public int Limit { get; init; }
}

internal sealed class ShardedRateLimitCounterStore
{
    private const int MaxExpiredProbeCount = 16;

    private readonly Shard[] _shards;

    internal ShardedRateLimitCounterStore(int maxEntries, int shardCount = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);

        Capacity = maxEntries;
        var effectiveShards = Math.Min(shardCount, maxEntries);
        _shards = new Shard[effectiveShards];
        var baseCapacity = maxEntries / effectiveShards;
        var remainder = maxEntries % effectiveShards;
        for (var i = 0; i < effectiveShards; i++)
        {
            _shards[i] = new Shard(baseCapacity + (i < remainder ? 1 : 0));
        }
    }

    internal int Capacity { get; }

    internal int Count
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _shards.Length; i++)
            {
                var shard = _shards[i];
                lock (shard.Gate)
                {
                    total += shard.Entries.Count;
                }
            }

            return total;
        }
    }

    internal void Clear()
    {
        for (var i = 0; i < _shards.Length; i++)
        {
            var shard = _shards[i];
            lock (shard.Gate)
            {
                shard.Entries.Clear();
                shard.Lru.Clear();
            }
        }
    }

    internal RateLimitResult RecordHit(
        ReadOnlySpan<char> counterKey,
        ResolvedRateLimit resolved,
        DateTimeOffset now,
        out bool evicted)
    {
        evicted = false;
        var window = resolved.Window;
        var windowTicks = Math.Max(TimeSpan.TicksPerSecond, window.Ticks);
        var bucketStartTicks = now.UtcTicks / windowTicks * windowTicks;
        var windowStart = new DateTimeOffset(bucketStartTicks, TimeSpan.Zero);

        var hash = ((IAlternateEqualityComparer<ReadOnlySpan<char>, string>)StringComparer.Ordinal).GetHashCode(counterKey);
        var shard = _shards[(uint)hash % (uint)_shards.Length];
        lock (shard.Gate)
        {
            var lookup = shard.Entries.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!lookup.TryGetValue(counterKey, out var node))
            {
                if (shard.Entries.Count >= shard.Capacity)
                {
                    PruneExpiredFromHead(shard, now);
                }

                if (shard.Entries.Count >= shard.Capacity)
                {
                    if (shard.Lru.First is not { } oldest)
                    {
                        return new RateLimitResult
                        {
                            IsAllowed = true,
                            RequestsRemaining = resolved.Limit,
                            WindowReset = windowStart + window,
                            RequestCount = 0,
                            Limit = resolved.Limit
                        };
                    }

                    shard.Lru.RemoveFirst();
                    shard.Entries.Remove(oldest.Value.Key);
                    evicted = true;
                }

                var materializedKey = counterKey.ToString();
                node = new LinkedListNode<CounterEntry>(new CounterEntry(materializedKey));
                shard.Lru.AddLast(node);
                shard.Entries[materializedKey] = node;
            }
            else if (node != shard.Lru.Last)
            {
                shard.Lru.Remove(node);
                shard.Lru.AddLast(node);
            }

            var entry = node.Value;
            if (entry.WindowStart != windowStart)
            {
                entry.WindowStart = windowStart;
                entry.Count = 0;
            }

            entry.WindowEnd = windowStart + window;
            var currentCount = ++entry.Count;
            var limit = resolved.Limit;

            return new RateLimitResult
            {
                IsAllowed = currentCount <= limit,
                RequestsRemaining = Math.Max(0, limit - currentCount),
                WindowReset = entry.WindowEnd,
                RequestCount = currentCount,
                Limit = limit
            };
        }
    }

    private static void PruneExpiredFromHead(Shard shard, DateTimeOffset now)
    {
        var current = shard.Lru.First;
        var checkedCount = 0;
        while (current is not null && checkedCount < MaxExpiredProbeCount)
        {
            var next = current.Next;
            checkedCount++;
            if (current.Value.WindowEnd <= now)
            {
                shard.Lru.Remove(current);
                shard.Entries.Remove(current.Value.Key);
            }

            current = next;
        }
    }

    private sealed class Shard(int capacity)
    {
        internal readonly object Gate = new();
        internal readonly int Capacity = capacity;
        internal readonly Dictionary<string, LinkedListNode<CounterEntry>> Entries = new(StringComparer.Ordinal);
        internal readonly LinkedList<CounterEntry> Lru = new();
    }

    private sealed class CounterEntry(string key)
    {
        internal string Key { get; } = key;
        internal DateTimeOffset WindowStart;
        internal DateTimeOffset WindowEnd;
        internal int Count;
    }
}
