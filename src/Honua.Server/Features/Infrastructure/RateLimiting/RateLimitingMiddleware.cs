// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.RateLimiting.Abstractions;

namespace Honua.Infrastructure.RateLimiting;

/// <summary>
/// Middleware for enforcing rate limits on incoming requests.
/// </summary>
internal sealed partial class RateLimitingMiddleware
{
    private const string ApiKeyKeyFamily = "api_key";
    private const string IpKeyFamily = "ip";
    private const string UnknownKeyFamily = "unknown";
    private readonly RequestDelegate _next;
    private readonly IRateLimitPolicyStore _policyStore;
    private readonly IDistributedCache _distributedCache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly RateLimitingOptions _options;
    private readonly ILogger<RateLimitingMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="policyStore">Rate limit policy store.</param>
    /// <param name="distributedCache">Distributed cache for rate limiting counters.</param>
    /// <param name="redis">Redis connection for distributed rate limiting.</param>
    /// <param name="options">Rate limiting options.</param>
    /// <param name="logger">Logger instance.</param>
    public RateLimitingMiddleware(
        RequestDelegate next,
        IRateLimitPolicyStore policyStore,
        IDistributedCache distributedCache,
        IConnectionMultiplexer? redis,
        IOptions<RateLimitingOptions> options,
        ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _policyStore = policyStore;
        _distributedCache = distributedCache;
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
    /// Determines the rate limit key for the request.
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

        // Check for API key-based rate limiting
        var apiKey = ExtractApiKey(context);
        if (!string.IsNullOrEmpty(apiKey))
        {
            return $"api_key:{apiKey}";
        }

        // Fall back to IP-based rate limiting
        return $"ip:{clientIp}";
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
                return await CheckRateLimitMemoryAsync(rateLimitKey, context);
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
    /// Checks rate limit using in-memory cache with fixed window algorithm.
    /// </summary>
    /// <param name="rateLimitKey">The rate limit key.</param>
    /// <param name="context">The HTTP context.</param>
    /// <returns>Rate limit check result.</returns>
    private async Task<RateLimitResult> CheckRateLimitMemoryAsync(string rateLimitKey, HttpContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        var cacheKey = $"rate_limit:{rateLimitKey}:{windowStart:yyyyMMddHHmm}";

        var counterBytes = await _distributedCache.GetAsync(cacheKey);
        var currentCount = 0;

        if (counterBytes != null)
        {
            currentCount = BitConverter.ToInt32(counterBytes, 0);
        }

        currentCount++;
        var limit = GetRateLimit(context);

        // Update counter with sliding expiration
        await _distributedCache.SetAsync(
            cacheKey,
            BitConverter.GetBytes(currentCount),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
            });

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
        context.Response.Headers.TryAdd("X-RateLimit-Limit", result.Limit.ToString());
        context.Response.Headers.TryAdd("X-RateLimit-Remaining", result.RequestsRemaining.ToString());
        context.Response.Headers.TryAdd("X-RateLimit-Reset", result.WindowReset.ToUnixTimeSeconds().ToString());
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
    /// Extracts API key from request headers or query parameters.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>API key if found.</returns>
    private static string? ExtractApiKey(HttpContext context)
    {
        // Check Authorization header for Bearer token
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader.Substring("Bearer ".Length).Trim();
        }

        // Check for API key in query parameters
        return context.Request.Query["api_key"].FirstOrDefault();
    }

    /// <summary>
    /// Splits a rate-limit key (<c>family:value</c>) into a fixed family label and a short
    /// correlation hash so neither the bearer token nor the raw IP appears in log output.
    /// </summary>
    private static (string Family, string Hash) SplitRateLimitKey(string? rateLimitKey)
    {
        if (string.IsNullOrEmpty(rateLimitKey))
        {
            return (UnknownKeyFamily, LogValueRedactor.Hash(null));
        }

        var separatorIndex = rateLimitKey.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return (UnknownKeyFamily, LogValueRedactor.Hash(rateLimitKey));
        }

        var family = rateLimitKey[..separatorIndex];
        var value = rateLimitKey[(separatorIndex + 1)..];

        return family switch
        {
            ApiKeyKeyFamily => (ApiKeyKeyFamily, LogValueRedactor.Hash(value)),
            IpKeyFamily => (IpKeyFamily, LogValueRedactor.Hash(value)),
            _ => (UnknownKeyFamily, LogValueRedactor.Hash(value))
        };
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
