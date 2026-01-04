// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Middleware that enforces rate limiting per IP address to prevent abuse and DoS attacks.
/// Uses a sliding window algorithm with configurable limits.
/// </summary>
internal sealed class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private readonly RateLimitOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly HashSet<IPAddress> _trustedProxies;
    private readonly ConcurrentDictionary<IPAddress, ClientRateLimit> _clients = new();
    private long _lastCleanupTick;

    public RateLimitingMiddleware(
        RequestDelegate next,
        ILogger<RateLimitingMiddleware> logger,
        IOptions<RateLimitOptions> options,
        IWebHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _trustedProxies = BuildTrustedProxySet(_options.TrustedProxies);
        _lastCleanupTick = Environment.TickCount64;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip rate limiting for health checks and development environment
        // This ensures monitoring systems and local development are not rate limited
        var trustProxyHeaders = ShouldTrustProxyHeaders(context);
        if (IsExemptPath(context.Request.Path) ||
            (_environment.IsDevelopment() && IsLocalRequest(context, trustProxyHeaders)))
        {
            await _next(context);
            return;
        }

        CleanupIfNeeded();

        var clientIp = GetClientIpAddress(context, trustProxyHeaders) ?? IPAddress.None;

        // Get or create rate limit tracker for this IP address
        // Uses sliding window algorithm to track request timestamps
        var clientLimit = _clients.GetOrAdd(clientIp, _ => new ClientRateLimit(_options.WindowSize));
        var now = DateTimeOffset.UtcNow;

        if (!clientLimit.TryAddRequest(_options.MaxRequestsPerWindow, now))
        {
            // Client has exceeded rate limit, return 429 Too Many Requests
            var retryAfter = clientLimit.GetRetryAfter(now);
            await HandleRateLimitExceededAsync(context, clientIp, retryAfter);
            return;
        }

        // Add standard rate limit headers to inform clients of their limits
        // These headers follow RFC 6585 and common industry practices
        context.Response.Headers["X-RateLimit-Limit"] = _options.MaxRequestsPerWindow.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, _options.MaxRequestsPerWindow - clientLimit.RequestCount).ToString();
        var resetAt = clientLimit.GetWindowResetUtc(now);
        context.Response.Headers["X-RateLimit-Reset"] = resetAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        await _next(context);
    }

    private static bool IsExemptPath(PathString path)
    {
        return path.StartsWithSegments("/healthz") ||
               path.StartsWithSegments("/_framework") ||
               path.StartsWithSegments("/favicon.ico");
    }

    private static bool IsLocalRequest(HttpContext context, bool trustProxyHeaders)
    {
        var request = context.Request;

        if (trustProxyHeaders && TryGetHeaderIp(request, "X-Forwarded-For", out var forwardedIp))
        {
            return IPAddress.IsLoopback(forwardedIp);
        }

        if (trustProxyHeaders && TryGetHeaderIp(request, "X-Real-IP", out var realIp))
        {
            return IPAddress.IsLoopback(realIp);
        }

        var connection = context.Connection;

        if (connection.RemoteIpAddress == null)
        {
            return true;
        }

        // localhost requests
        if (connection.RemoteIpAddress?.Equals(connection.LocalIpAddress) == true)
            return true;

        // 127.0.0.1
        return IPAddress.IsLoopback(connection.RemoteIpAddress ?? IPAddress.None);
    }

    private static bool TryGetHeaderIp(HttpRequest request, string headerName, out IPAddress ip)
    {
        if (request.Headers.TryGetValue(headerName, out var headerValue))
        {
            var firstIp = headerValue.ToString().Split(',')[0].Trim();
            if (IPAddress.TryParse(firstIp, out IPAddress? parsedIp) && parsedIp is not null)
            {
                ip = parsedIp;
                return true;
            }
        }

        ip = IPAddress.None;
        return false;
    }

    private static IPAddress? GetClientIpAddress(HttpContext context, bool trustProxyHeaders)
    {
        var request = context.Request;

        // Check X-Forwarded-For header first (when behind proxy)
        // Format: X-Forwarded-For: client, proxy1, proxy2
        // We want the first (leftmost) IP which is the original client
        if (trustProxyHeaders && request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var firstIp = forwardedFor.ToString().Split(',')[0].Trim();
            if (IPAddress.TryParse(firstIp, out var ip))
                return ip;
        }

        // Check X-Real-IP header (single IP, set by some proxies)
        if (trustProxyHeaders && request.Headers.TryGetValue("X-Real-IP", out var realIp))
        {
            if (IPAddress.TryParse(realIp.ToString(), out var ip))
                return ip;
        }

        // Fall back to connection remote IP (direct connection or misconfigured proxy)
        return context.Connection.RemoteIpAddress;
    }

    private bool ShouldTrustProxyHeaders(HttpContext context)
    {
        if (!_options.TrustProxyHeaders)
        {
            return false;
        }

        if (_trustedProxies.Count == 0)
        {
            return false;
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        return remoteIp != null && _trustedProxies.Contains(remoteIp);
    }

    private static HashSet<IPAddress> BuildTrustedProxySet(string[] trustedProxies)
    {
        var proxies = new HashSet<IPAddress>();
        foreach (var proxy in trustedProxies ?? Array.Empty<string>())
        {
            if (IPAddress.TryParse(proxy, out var ip))
            {
                proxies.Add(ip);
            }
        }

        return proxies;
    }

    private void CleanupIfNeeded()
    {
        var intervalMs = (long)_options.CleanupInterval.TotalMilliseconds;
        if (intervalMs <= 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastCleanupTick);
        if (now - last < intervalMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupTick, now, last) != last)
        {
            return;
        }

        var currentTime = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var (ip, client) in _clients)
        {
            if (client.IsExpired(currentTime))
            {
                if (_clients.TryRemove(ip, out _))
                {
                    removed++;
                }
            }
        }

        if (removed > 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            Log.RateLimitCacheCleanup(_logger, removed);
        }
    }

    private async Task HandleRateLimitExceededAsync(HttpContext context, IPAddress clientIp, TimeSpan retryAfter)
    {
        Log.RateLimitExceeded(_logger, clientIp.ToString(), _options.MaxRequestsPerWindow, _options.WindowSize.TotalMinutes);

        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var detail = $"Rate limit exceeded. Maximum {_options.MaxRequestsPerWindow} requests per {_options.WindowSize.TotalMinutes} minutes.";
        await ProtocolErrorWriter.WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "Too Many Requests", detail);
    }

}

/// <summary>
/// Tracks rate limiting information for a single client IP address.
/// </summary>
internal sealed class ClientRateLimit
{
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly TimeSpan _windowSize;

    public ClientRateLimit(TimeSpan windowSize)
    {
        _windowSize = windowSize;
    }

    public int RequestCount { get; private set; }
    public bool TryAddRequest(int maxRequests, DateTimeOffset now)
    {
        lock (_lock)
        {
            var windowStart = now - _windowSize;

            // Remove expired requests
            while (_requests.Count > 0 && _requests.Peek() < windowStart)
            {
                _requests.Dequeue();
            }

            RequestCount = _requests.Count;

            // Check if we can add a new request
            if (RequestCount >= maxRequests)
            {
                return false;
            }

            _requests.Enqueue(now);
            RequestCount = _requests.Count;
            return true;
        }
    }

    public DateTimeOffset GetWindowResetUtc(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_requests.Count == 0)
            {
                return now;
            }

            var resetAt = _requests.Peek().Add(_windowSize);
            return resetAt > now ? resetAt : now;
        }
    }

    public TimeSpan GetRetryAfter(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_requests.Count == 0)
            {
                return TimeSpan.Zero;
            }

            var resetAt = _requests.Peek().Add(_windowSize);
            var remaining = resetAt - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool IsExpired(DateTimeOffset now)
    {
        lock (_lock)
        {
            var windowStart = now - _windowSize;
            return _requests.Count == 0 || _requests.Peek() < windowStart;
        }
    }
}

/// <summary>
/// Configuration options for rate limiting.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>
    /// Maximum number of requests per window per IP address.
    /// Default: 1000 requests per 10 minutes.
    /// </summary>
    public int MaxRequestsPerWindow { get; set; } = 1000;

    /// <summary>
    /// Size of the rate limiting window.
    /// Default: 10 minutes.
    /// </summary>
    public TimeSpan WindowSize { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether to trust proxy headers (X-Forwarded-For, X-Real-IP) for client IP detection.
    /// Default: false.
    /// </summary>
    public bool TrustProxyHeaders { get; set; }

    /// <summary>
    /// List of trusted proxy IPs allowed to supply forwarded headers.
    /// Empty list disables trusting forwarded headers to prevent spoofing.
    /// </summary>
    public string[] TrustedProxies { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Interval for cleaning up idle client entries to prevent unbounded memory growth.
    /// Default: 10 minutes.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Extension methods for rate limiting middleware.
/// </summary>
public static class RateLimitingMiddlewareExtensions
{
    /// <summary>
    /// Adds rate limiting middleware to the application pipeline.
    /// </summary>
    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RateLimitingMiddleware>();
    }
}

/// <summary>
/// High-performance logging for rate limiting events.
/// </summary>
internal static partial class Log
{
    /// <summary>
    /// Logs when a client exceeds the configured rate limit.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="clientIp">The IP address of the client that exceeded the rate limit.</param>
    /// <param name="maxRequests">The maximum number of requests allowed.</param>
    /// <param name="windowMinutes">The time window in minutes for the rate limit.</param>
    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Rate limit exceeded for client {ClientIp}. Limit: {MaxRequests} requests per {WindowMinutes} minutes")]
    public static partial void RateLimitExceeded(ILogger logger, string clientIp, int maxRequests, double windowMinutes);

    /// <summary>
    /// Logs when the rate limit cache cleanup process removes expired client entries.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="expiredCount">The number of expired client entries that were removed.</param>
    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Debug,
        Message = "Rate limit cache cleanup removed {ExpiredCount} expired client entries")]
    public static partial void RateLimitCacheCleanup(ILogger logger, int expiredCount);
}
