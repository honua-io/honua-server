// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Server.Features.Operations;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.RateLimiting;

internal sealed class RateLimitingDependencies
{
    public required IRateLimitPolicyStore PolicyStore { get; init; }
    public required IOptions<RateLimitingOptions> Options { get; init; }
    public required ILogger<RateLimitingMiddleware> Logger { get; init; }
    public TimeProvider? TimeProvider { get; init; }
    public ShardedRateLimitCounterStore? CounterStore { get; init; }
}

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

    private readonly RequestDelegate _next;
    private readonly IRateLimitPolicyStore _policyStore;
    private readonly RateLimitingOptions _options;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ShardedRateLimitCounterStore _counterStore;
    private long _lastMemoryCapacityLogTicks;

    public RateLimitingMiddleware(
        RequestDelegate next,
        RateLimitingDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _next = next;
        _policyStore = dependencies.PolicyStore;
        _options = dependencies.Options.Value;
        _logger = dependencies.Logger;
        _timeProvider = dependencies.TimeProvider ?? TimeProvider.System;
        _counterStore = dependencies.CounterStore ?? SharedCounterStore;
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
                return FormatAuthenticatedSubjectKey(new AuthenticatedSubjectKeyParts
                {
                    TenantId = tenantId,
                    AuthenticationScheme = authenticationScheme,
                    IssuerScope = issuerScope,
                    AuthenticatedSubject = authenticatedSubject
                });
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

    private async Task<RateLimitResult> CheckRateLimitAsync(string rateLimitKey, HttpContext context)
    {
        try
        {
            var (subjectLimit, fromPolicyStore) = await ResolveSubjectLimitAsync(context);
            var subjectResult = CheckRateLimitMemory(rateLimitKey.AsSpan(), subjectLimit);

            var endpointLimit = fromPolicyStore
                ? ResolveExplicitEndpointLimit(context)
                : ResolveEndpointOrCategoryLimit(context);
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
        var outcome = _counterStore.RecordHit(counterKey, resolved, now);
        if (outcome.Evicted)
        {
            LogMemoryCounterEviction(now);
        }

        return outcome.Result;
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
