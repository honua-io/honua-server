// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Health;

/// <summary>
/// Represents the aggregate result of the server readiness checks.
/// </summary>
public sealed record ReadinessResult
{
    /// <summary>Gets whether the service is ready to accept traffic.</summary>
    public required bool IsReady { get; init; }

    /// <summary>Gets the HTTP status code associated with the result.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Gets the human-readable readiness message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the exception that caused the check to fail, if any.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Creates a successful readiness result.</summary>
    /// <returns>A successful readiness result.</returns>
    public static ReadinessResult Ready() => new()
    {
        IsReady = true,
        StatusCode = 200,
        Message = "Ready"
    };

    /// <summary>Creates a failed readiness result.</summary>
    /// <param name="reason">Reason for failure.</param>
    /// <param name="exception">Exception that caused the failure.</param>
    /// <returns>A failed readiness result.</returns>
    public static ReadinessResult NotReady(string reason, Exception? exception = null) => new()
    {
        IsReady = false,
        StatusCode = 503,
        Message = $"Not Ready - {reason}",
        Exception = exception
    };
}
