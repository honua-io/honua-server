// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Sanitized delivery error summaries that are safe for admin API responses.
/// </summary>
public static class AlertDeliveryErrorSummaries
{
    /// <summary>
    /// Summary returned when the most recent delivery failure indicates rate limiting.
    /// </summary>
    public const string RateLimited = "Delivery rate limited.";

    /// <summary>
    /// Summary returned for delivery failures without a more specific safe classification.
    /// </summary>
    public const string Failed = "Delivery failed.";

    /// <summary>
    /// Converts a raw provider delivery error into a sanitized response summary.
    /// </summary>
    /// <param name="error">Raw delivery error text from the dispatch store.</param>
    /// <returns>A sanitized summary, or null when no error was provided.</returns>
    public static string? ToSanitizedSummary(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        return IsRateLimited(error) ? RateLimited : Failed;
    }

    /// <summary>
    /// Determines whether a raw or sanitized delivery error indicates rate limiting.
    /// </summary>
    /// <param name="error">Delivery error text.</param>
    /// <returns>True when the error indicates a rate-limit failure.</returns>
    public static bool IsRateLimited(string? error)
    {
        return error is not null &&
               (error.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
    }
}
