// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Backpressure;

/// <summary>
/// Stable machine codes and transport metadata names for retryable backpressure responses.
/// </summary>
public static class BackpressureMetadata
{
    public const string RateLimitExceededCode = "rate_limit_exceeded";
    public const string ServiceUnavailableCode = "service_unavailable";
    public const string ErrorCodeKey = "honua-error-code";
    public const string RetryableKey = "honua-retryable";
    public const string RetryAfterKey = "retry-after";
    public const string CorrelationIdKey = "honua-correlation-id";
}
