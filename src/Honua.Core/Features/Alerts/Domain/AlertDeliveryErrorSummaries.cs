// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Domain;

public static class AlertDeliveryErrorSummaries
{
    public const string RateLimited = "Delivery rate limited.";

    public const string Failed = "Delivery failed.";

    public static string? ToSanitizedSummary(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        return IsRateLimited(error) ? RateLimited : Failed;
    }

    public static bool IsRateLimited(string? error)
    {
        return error is not null &&
               (error.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
    }
}
