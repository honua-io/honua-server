// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Health;

public sealed record ReadinessResult
{
    public required bool IsReady { get; init; }

    public required int StatusCode { get; init; }

    public required string Message { get; init; }

    public Exception? Exception { get; init; }

    public string? ReasonCode { get; init; }

    public static ReadinessResult Ready() => new()
    {
        IsReady = true,
        StatusCode = 200,
        Message = "Ready"
    };

    public static ReadinessResult NotReady(string reason, Exception? exception = null) => new()
    {
        IsReady = false,
        StatusCode = 503,
        Message = $"Not Ready - {reason}",
        Exception = exception
    };

    public static ReadinessResult NotReady(string reason, string reasonCode, Exception? exception = null)
        => NotReady(reason, exception) with { ReasonCode = reasonCode };
}
