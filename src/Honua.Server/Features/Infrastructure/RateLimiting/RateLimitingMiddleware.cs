// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;

namespace Honua.Infrastructure.RateLimiting;

/// <summary>
/// Middleware for enforcing rate limits on incoming requests.
/// </summary>
/// <remarks>
/// Requests are partitioned independently per tenant, per authenticated user/API key,
/// and per source IP for anonymous traffic (issue #355). When an endpoint declares an
/// explicit per-endpoint limit (<see cref="RateLimitAttribute"/>), its counter is further
/// partitioned by that endpoint's policy scope so per-endpoint limits meter only that
/// endpoint's traffic (issue #2779); tier/global limits stay shared across endpoints for
/// the subject. Limits are off by default and must be enabled and tuned through the
/// <c>RateLimiting</c> configuration section; the MVP posture remains edge enforcement
/// (ADR-0004) unless an operator opts in.
/// </remarks>
internal sealed partial class RateLimitingMiddleware
{
    private const string TenantKeyFamily = "tenant";
    private const string UserKeyFamily = "user";
    private const string IpKeyFamily = "ip";
    private const string UnknownKeyFamily = "unknown";

    /// <summary>
    /// Soft cap on process-local counter entries before stale windows are pruned.
    /// </summary>
    private const int MaxMemoryCounterEntries = 10_000;

    /// <summary>
    /// Process-local fixed-window counters used when Redis is unavailable. Counters
    /// are mutated under a per-entry lock so concurrent requests for the same key
    /// cannot lose increments (a get/set round-trip through a cache is not atomic).
    /// </summary>
    private static readonly ConcurrentDictionary<string, FixedWindowCounter> _memoryCounters = new();

    private readonly RequestDelegate _next;
    private readonly IRateLimitPolicyStore _policyStore;
    private readonly IConnectionMultiplexer? _redis;
    private readonly RateLimitingOptions _options;
    private readonly ILogger<RateLimitingMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="policyStore">Rate limit policy store.</param>
    /// <param name="options">Rate limiting options.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="redis">
    /// Optional Redis connection for distributed (cross-node) rate limiting. Registered only
    /// when durable coordination is configured, so it is passed in optionally by
    /// <see cref="RateLimitingMiddlewareExtensions.UseRateLimiting"/> rather than required via
    /// injection; when <see langword="null"/> the middleware falls back to process-local
    /// fixed-window counters.
    /// </param>
    public RateLimitingMiddleware(
        RequestDelegate next,
        IRateLimitPolicyStore policyStore,
        IOptions<RateLimitingOptions> options,
        ILogger<RateLimitingMiddleware> logger,
        IConnectionMultiplexer? redis)
    {
        _next = next;
        _policyStore = policyStore;
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing the async operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
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

    /// <summary>
    /// Determines the rate limit key for the request. The key is partitioned by tenant
    /// (when one is resolved) and then by authenticated user/API key identity, falling
    /// back to the source IP for anonymous traffic (issue #355).
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>Rate limit key or null if no rate limiting should be applied.</returns>
    private static string? DetermineRateLimitKey(HttpContext context)
    {
        // Skip rate limiting for health checks and metrics endpoints
        var path = context.Request.Path.Value?.ToLowerInvariant();
        if (path is "/health" or "/metrics" or "/ready")
        {
            return null;
        }

        // Use client IP as the primary rate limiting key
        var clientIp = GetClientIpAddress(context);
        if (string.IsNullOrEmpty(clientIp))
        {
            return null;
        }

        // Tenant scope (resolved by TenantContextMiddleware, which runs earlier in the
        // pipeline). When present it prefixes the bucket so two tenants sharing an
        // identity provider — or an anonymous IP straddling tenants — never share a
        // counter. The id is a stable opaque tenant identifier, never a secret.
        var tenantPrefix = string.Empty;
        var tenantContext = context.RequestServices?.GetService(typeof(ITenantContext)) as ITenantContext;
        if (!string.IsNullOrEmpty(tenantContext?.TenantId))
        {
            tenantPrefix = $"{TenantKeyFamily}:{tenantContext.TenantId}|";
        }

        // Key on the authenticated principal when one is present. Never derive the
        // bucket from raw, unauthenticated credentials (Authorization header or
        // ?api_key=): an attacker could mint a fresh bucket per request by sending
        // a random token, bypassing the IP-based limit entirely, and the raw secret
        // would be persisted verbatim into the cache/Redis key space.
        var identity = context.User?.Identity;
        if (identity?.IsAuthenticated == true && !string.IsNullOrEmpty(identity.Name))
        {
            return $"{tenantPrefix}{UserKeyFamily}:{identity.Name}";
        }

        // Fall back to IP-based rate limiting for unauthenticated requests
        return $"{tenantPrefix}{IpKeyFamily}:{clientIp}";
    }

    /// <summary>
    /// Checks if the request is within rate limits.
    /// </summary>
    /// <param name="rateLimitKey">The rate limit key.</param>
    /// <param name="context">The HTTP context.</param>
    /// <returns>Rate limit check result.</returns>
    private async Task<RateLimitResult> CheckRateLimitAsync(string rateLimitKey, HttpContext context)
    {
        try
        {
            // Resolve the applicable limit and window once: an explicit per-endpoint override,
            // else the most specific matching tier policy (api-key > tenant > plan), else the
            // global default. Tier resolution feeds the same distributed counter so the limit is
            // enforced consistently across instances.
            var resolved = await ResolveLimitAsync(context);

            // A per-endpoint limit meters only that endpoint, so its counter is partitioned by the
            // endpoint's policy scope (issue #2779). Without this, high-volume traffic to one
            // endpoint drained every other endpoint's allowance for the same subject — e.g. ordinary
            // Console page traffic exhausting the OIDC authorize-url 5/min limit. Tier/global limits
            // carry no scope and keep the shared subject counter.
            var counterKey = string.IsNullOrEmpty(resolved.Scope)
                ? rateLimitKey
                : $"{rateLimitKey}|policy:{resolved.Scope}";

            // Use Redis-based sliding window if available, otherwise fall back to fixed window
            if (_redis != null)
            {
                return await CheckRateLimitRedisAsync(counterKey, resolved);
            }
            else
            {
                return CheckRateLimitMemory(counterKey, resolved);
            }
        }
        catch (Exception ex)
        {
            var (keyFamily, keyHash) = SplitRateLimitKey(rateLimitKey);
            RateLimitingLog.RateLimitCheckFailed(_logger, keyFamily, keyHash, ex);

            // Allow request if rate limiting fails to avoid blocking legitimate traffic
            return new RateLimitResult
            {
                IsAllowed = true,
                RequestsRemaining = _options.GlobalRequestsPerMinute,
                WindowReset = DateTimeOffset.UtcNow.AddMinutes(1)
            };
        }
    }

    /// <summary>
    /// Checks rate limit using Redis sliding window algorithm.
    /// </summary>
    /// <param name="counterKey">The counter key (subject identity plus any per-endpoint policy scope).</param>
    /// <param name="resolved">The resolved limit and window for this request.</param>
    /// <returns>Rate limit check result.</returns>
    private async Task<RateLimitResult> CheckRateLimitRedisAsync(string counterKey, ResolvedRateLimit resolved)
    {
        var database = _redis!.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var window = resolved.Window;
        var windowStart = now - window;

        var cacheKey = $"rate_limit:{counterKey}";
        var windowKey = $"{cacheKey}:window";

        // Use Redis sorted set for sliding window
        var pipeline = database.CreateTransaction();

        // Remove entries older than the window
        var removeOldTask = pipeline.SortedSetRemoveRangeByScoreAsync(
            windowKey,
            0,
            windowStart.ToUnixTimeMilliseconds());

        // Add current request
        var addCurrentTask = pipeline.SortedSetAddAsync(
            windowKey,
            Guid.NewGuid().ToString(),
            now.ToUnixTimeMilliseconds());

        // Count requests in window
        var countTask = pipeline.SortedSetLengthAsync(windowKey);

        // Set expiration to twice the window so stale entries self-evict.
        var expireTask = pipeline.KeyExpireAsync(windowKey, window + window);

        await pipeline.ExecuteAsync();

        var requestCount = await countTask;
        var limit = resolved.Limit;

        return new RateLimitResult
        {
            IsAllowed = requestCount <= limit,
            RequestsRemaining = Math.Max(0, limit - (int)requestCount),
            WindowReset = now + window,
            RequestCount = (int)requestCount,
            Limit = limit
        };
    }

    /// <summary>
    /// Checks rate limit using process-local counters with a fixed window algorithm.
    /// Increments happen under a per-counter lock so concurrent requests for the
    /// same key are counted exactly (the previous get-increment-set round trip lost
    /// updates under concurrency, letting clients exceed the limit).
    /// </summary>
    /// <param name="counterKey">The counter key (subject identity plus any per-endpoint policy scope).</param>
    /// <param name="resolved">The resolved limit and window for this request.</param>
    /// <returns>Rate limit check result.</returns>
    private static RateLimitResult CheckRateLimitMemory(string counterKey, ResolvedRateLimit resolved)
    {
        var now = DateTimeOffset.UtcNow;
        var window = resolved.Window;

        // Align the fixed window to epoch-anchored buckets so an arbitrary policy window
        // (not just one minute) is deterministic and shared across instances backed by the
        // same store.
        var windowTicks = Math.Max(TimeSpan.TicksPerSecond, window.Ticks);
        var bucketStartTicks = now.UtcTicks / windowTicks * windowTicks;
        var windowStart = new DateTimeOffset(bucketStartTicks, TimeSpan.Zero);

        var counter = _memoryCounters.GetOrAdd(counterKey, static _ => new FixedWindowCounter());
        int currentCount;
        lock (counter)
        {
            if (counter.WindowStart != windowStart)
            {
                counter.WindowStart = windowStart;
                counter.Count = 0;
            }

            currentCount = ++counter.Count;
        }

        PruneExpiredMemoryCounters(windowStart);

        var limit = resolved.Limit;

        return new RateLimitResult
        {
            IsAllowed = currentCount <= limit,
            RequestsRemaining = Math.Max(0, limit - currentCount),
            WindowReset = windowStart + window,
            RequestCount = currentCount,
            Limit = limit
        };
    }

    /// <summary>
    /// Opportunistically removes counters from previous windows once the dictionary
    /// grows beyond <see cref="MaxMemoryCounterEntries"/>, bounding memory use.
    /// </summary>
    private static void PruneExpiredMemoryCounters(DateTimeOffset currentWindowStart)
    {
        if (_memoryCounters.Count <= MaxMemoryCounterEntries)
        {
            return;
        }

        foreach (var entry in _memoryCounters)
        {
            bool stale;
            lock (entry.Value)
            {
                stale = entry.Value.WindowStart < currentWindowStart;
            }

            if (stale)
            {
                _memoryCounters.TryRemove(entry);
            }
        }
    }

    /// <summary>
    /// Mutable fixed-window counter state; mutated only under a lock on the instance.
    /// </summary>
    private sealed class FixedWindowCounter
    {
        public DateTimeOffset WindowStart;
        public int Count;
    }

    /// <summary>
    /// Resolves the limit and window for the current request using the documented precedence:
    /// an explicit per-endpoint <see cref="RateLimitAttribute"/> override, then the most specific
    /// matching tier policy (api-key &gt; tenant &gt; plan) from the policy store, then the global
    /// default. Tier policies carry their own <see cref="RateLimitPolicy.WindowDuration"/>; the
    /// endpoint and global fallbacks use a one-minute window.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The resolved limit and window.</returns>
    private async Task<ResolvedRateLimit> ResolveLimitAsync(HttpContext context)
    {
        // A per-endpoint override is the most explicit operator intent and wins outright. Its
        // counter is scoped to the endpoint so it meters only that endpoint's traffic (issue #2779).
        var endpoint = context.GetEndpoint();
        var endpointRateLimit = endpoint?.Metadata.GetMetadata<RateLimitAttribute>();
        if (endpointRateLimit != null)
        {
            return new ResolvedRateLimit(
                endpointRateLimit.RequestsPerMinute,
                TimeSpan.FromMinutes(1),
                GetEndpointScope(endpoint));
        }

        // Subject-tier policies (per-plan / per-tenant / per-api-key) drive the limit when one
        // matches. Resolution order is centralized in RateLimitPolicyResolver so the precedence is
        // identical everywhere it is applied. Tier limits meter the subject across all endpoints, so
        // they carry no endpoint scope.
        var policies = await _policyStore.ListPoliciesAsync(context.RequestAborted);
        if (policies is { Count: > 0 })
        {
            var descriptor = BuildRequestDescriptor(context);
            var policy = RateLimitPolicyResolver.Resolve(policies, descriptor);
            if (policy is not null && policy.RequestsPerWindow > 0 && policy.WindowDuration > TimeSpan.Zero)
            {
                return new ResolvedRateLimit(policy.RequestsPerWindow, policy.WindowDuration, string.Empty);
            }
        }

        // Global default (one-minute window), shared across endpoints for the subject.
        return new ResolvedRateLimit(_options.GlobalRequestsPerMinute, TimeSpan.FromMinutes(1), string.Empty);
    }

    /// <summary>
    /// Derives a stable per-endpoint policy scope used to partition the rate-limit counter so that
    /// an endpoint's explicit limit meters only that endpoint's traffic. Prefers the route template
    /// (stable across hosts and request values), falling back to the endpoint display name.
    /// </summary>
    /// <param name="endpoint">The matched endpoint carrying the <see cref="RateLimitAttribute"/>.</param>
    /// <returns>A non-empty scope identifier for the endpoint.</returns>
    private static string GetEndpointScope(Endpoint? endpoint)
    {
        if (endpoint is RouteEndpoint routeEndpoint)
        {
            var routeTemplate = routeEndpoint.RoutePattern.RawText;
            if (!string.IsNullOrEmpty(routeTemplate))
            {
                return routeTemplate;
            }
        }

        return string.IsNullOrEmpty(endpoint?.DisplayName) ? "endpoint" : endpoint.DisplayName;
    }

    /// <summary>
    /// Builds the subject-tier descriptor (api-key identity, tenant, plan) for the current request
    /// so the policy resolver can select the applicable tier. Values are stable opaque identifiers,
    /// never raw secrets.
    /// </summary>
    private static RateLimitRequestDescriptor BuildRequestDescriptor(HttpContext context)
    {
        var identity = context.User?.Identity;
        var apiKey = identity?.IsAuthenticated == true && !string.IsNullOrEmpty(identity.Name)
            ? identity.Name
            : null;

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

    /// <summary>
    /// Adds rate limiting headers to the response.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="result">Rate limit result.</param>
    private static void AddRateLimitHeaders(HttpContext context, RateLimitResult result)
    {
        context.Response.Headers.TryAdd("X-RateLimit-Limit", result.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        context.Response.Headers.TryAdd("X-RateLimit-Remaining", result.RequestsRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        context.Response.Headers.TryAdd("X-RateLimit-Reset", result.WindowReset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Handles rate limit exceeded scenario.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="result">Rate limit result.</param>
    /// <returns>A task representing the async operation.</returns>
    private async Task HandleRateLimitExceededAsync(HttpContext context, RateLimitResult result)
    {
        var (keyFamily, keyHash) = SplitRateLimitKey(DetermineRateLimitKey(context));
        RateLimitingLog.RateLimitExceeded(_logger, keyFamily, keyHash, result.RequestCount, result.Limit);

        AddRateLimitHeaders(context, result);

        // RFC 9110 Retry-After: advise clients how long to wait (in whole seconds, never
        // negative) before retrying. Mirrors the X-RateLimit-Reset window boundary.
        var retryAfterSeconds = Math.Max(0, (int)Math.Ceiling((result.WindowReset - DateTimeOffset.UtcNow).TotalSeconds));
        context.Response.Headers.RetryAfter =
            retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        context.Response.ContentType = "application/json";

        var response = new RateLimitExceededResponse
        {
            Error = "rate_limit_exceeded",
            Message = "Too many requests. Please try again later.",
            Details = new RateLimitExceededDetails
            {
                Limit = result.Limit,
                WindowReset = result.WindowReset.ToUnixTimeSeconds()
            }
        };

        await context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(
                response,
                RateLimitingJsonContext.Default.RateLimitExceededResponse));
    }

    /// <summary>
    /// Gets the client IP address from the request.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>Client IP address.</returns>
    private static string? GetClientIpAddress(HttpContext context)
    {
        // Trust the connection IP only. If forwarded headers are enabled, the
        // ASP.NET Core forwarded-headers middleware will already have rewritten
        // RemoteIpAddress after validating the proxy against the known-proxy list.
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            return null;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
        {
            remoteIp = remoteIp.MapToIPv4();
        }

        return remoteIp.ToString();
    }

    /// <summary>
    /// Splits a rate-limit key into a fixed family label and a short correlation hash so
    /// neither the tenant id, principal name, nor raw IP appears in log output. Keys may
    /// be tenant-prefixed (<c>tenant:&lt;id&gt;|user:&lt;name&gt;</c>); the most specific
    /// (rightmost) family is reported and the whole key is hashed for correlation.
    /// </summary>
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

/// <summary>
/// The limit and window resolved for a request after applying endpoint, tier, and global
/// precedence.
/// </summary>
/// <param name="Limit">Maximum requests permitted within <paramref name="Window"/>.</param>
/// <param name="Window">The rolling/fixed window the limit applies over.</param>
/// <param name="Scope">
/// The policy scope that partitions the counter. Empty for subject-wide (tier/global) limits;
/// a per-endpoint identifier when an endpoint declares its own limit so that limit meters only
/// that endpoint's traffic (issue #2779).
/// </param>
internal readonly record struct ResolvedRateLimit(int Limit, TimeSpan Window, string Scope);

/// <summary>
/// Result of a rate limit check.
/// </summary>
internal sealed class RateLimitResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the request is allowed.
    /// </summary>
    public required bool IsAllowed { get; set; }

    /// <summary>
    /// Gets or sets the number of requests remaining in the current window.
    /// </summary>
    public required int RequestsRemaining { get; set; }

    /// <summary>
    /// Gets or sets when the current rate limiting window resets.
    /// </summary>
    public required DateTimeOffset WindowReset { get; set; }

    /// <summary>
    /// Gets or sets the current request count in the window.
    /// </summary>
    public int RequestCount { get; set; }

    /// <summary>
    /// Gets or sets the rate limit value.
    /// </summary>
    public int Limit { get; set; }
}

/// <summary>
/// Attribute for specifying endpoint-specific rate limits.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RateLimitAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitAttribute"/> class.
    /// </summary>
    /// <param name="requestsPerMinute">Requests per minute for this endpoint.</param>
    public RateLimitAttribute(int requestsPerMinute)
    {
        RequestsPerMinute = requestsPerMinute;
    }

    /// <summary>
    /// Gets the requests per minute limit for the endpoint.
    /// </summary>
    public int RequestsPerMinute { get; }
}
