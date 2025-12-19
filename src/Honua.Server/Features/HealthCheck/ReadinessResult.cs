// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Result of a readiness health check
/// </summary>
public sealed record ReadinessResult
{
    /// <summary>
    /// Whether the service is ready to accept traffic
    /// </summary>
    public required bool IsReady { get; init; }

    /// <summary>
    /// HTTP status code to return
    /// </summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// Response message to return to client
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Exception that occurred during health check, if any
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Create a successful readiness result
    /// </summary>
    /// <returns>Successful readiness result</returns>
    public static ReadinessResult Ready() => new()
    {
        IsReady = true,
        StatusCode = 200,
        Message = "Ready"
    };

    /// <summary>
    /// Create a failed readiness result
    /// </summary>
    /// <param name="reason">Reason for failure</param>
    /// <param name="exception">Exception that caused the failure</param>
    /// <returns>Failed readiness result</returns>
    public static ReadinessResult NotReady(string reason, Exception? exception = null) => new()
    {
        IsReady = false,
        StatusCode = 503, // Service Unavailable
        Message = $"Not Ready - {reason}",
        Exception = exception
    };
}
