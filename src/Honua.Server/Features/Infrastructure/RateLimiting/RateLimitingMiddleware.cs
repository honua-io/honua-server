// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;

namespace Honua.Infrastructure.RateLimiting;

/// <summary>
/// Middleware for enforcing rate limits on incoming requests.
/// </summary>
/// <remarks>
/// Requests are partitioned independently per tenant, per authenticated user/API key,
/// and per source IP for anonymous traffic (issue #355). Limits are off by default and
/// must be enabled and tuned through the <c>RateLimiting</c> configuration section; the
/// MVP posture remains edge enforcement (ADR-0004) unless an operator opts in.
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
            // Use Redis-based sliding window if available, otherwise fall back to fixed window
            if (_redis != null)
            {
                return await CheckRateLimitRedisAsync(rateLimitKey, context);
            }
            else
            {
                return CheckRateLimitMemory(rateLimitKey, context);
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
    /// <param name="rateLimitKey">The rate limit key.</param>
    /// <param name="context">The HTTP context.</param>
    /// <returns>Rate limit check result.</returns>
    private async Task<RateLimitResult> CheckRateLimitRedisAsync(string rateLimitKey, HttpContext context)
    {
        var database = _redis!.GetDatabase();
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddMinutes(-1);

        var cacheKey = $"rate_limit:{rateLimitKey}";
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

        // Set expiration
        var expireTask = pipeline.KeyExpireAsync(windowKey, TimeSpan.FromMinutes(2));

        await pipeline.ExecuteAsync();

        var requestCount = await countTask;
        var limit = GetRateLimit(context);

        return new RateLimitResult
        {
            IsAllowed = requestCount <= limit,
            RequestsRemaining = Math.Max(0, limit - (int)requestCount),
            WindowReset = now.AddMinutes(1),
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
    /// <param name="rateLimitKey">The rate limit key.</param>
    /// <param name="context">The HTTP context.</param>
    /// <returns>Rate limit check result.</returns>
    private RateLimitResult CheckRateLimitMemory(string rateLimitKey, HttpContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);

        var counter = _memoryCounters.GetOrAdd(rateLimitKey, static _ => new FixedWindowCounter());
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

        var limit = GetRateLimit(context);

        return new RateLimitResult
        {
            IsAllowed = currentCount <= limit,
            RequestsRemaining = Math.Max(0, limit - currentCount),
            WindowReset = windowStart.AddMinutes(1),
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
    /// Gets the rate limit for the current request.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>Rate limit value.</returns>
    private int GetRateLimit(HttpContext context)
    {
        // Check for endpoint-specific rate limits
        var endpoint = context.GetEndpoint();
        var endpointRateLimit = endpoint?.Metadata.GetMetadata<RateLimitAttribute>();
        if (endpointRateLimit != null)
        {
            return endpointRateLimit.RequestsPerMinute;
        }

        // Use global rate limit
        return _options.GlobalRequestsPerMinute;
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
