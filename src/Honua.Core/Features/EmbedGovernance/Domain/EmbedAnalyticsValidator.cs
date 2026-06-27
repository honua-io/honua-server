// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

/// <summary>
/// Outcome of validating a redacted embed analytics event.
/// </summary>
public sealed record EmbedAnalyticsValidationResult
{
    /// <summary>Whether the event is acceptable for ingestion.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Validation error when <see cref="IsValid"/> is <c>false</c>.</summary>
    public string? Error { get; init; }

    /// <summary>A valid result singleton.</summary>
    public static EmbedAnalyticsValidationResult Valid { get; } = new() { IsValid = true };

    /// <summary>
    /// Builds an invalid result with the supplied message.
    /// </summary>
    /// <param name="error">The validation error.</param>
    /// <returns>An invalid result.</returns>
    public static EmbedAnalyticsValidationResult Invalid(string error) => new()
    {
        IsValid = false,
        Error = error,
    };
}

/// <summary>
/// Validates redacted embed analytics events. Enforces field bounds and, most
/// importantly, that no field leaks raw browser API-key material.
/// </summary>
public static class EmbedAnalyticsValidator
{
    private const int MaxFieldLength = 512;

    /// <summary>
    /// Validates a single analytics event.
    /// </summary>
    /// <param name="analyticsEvent">The event to validate.</param>
    /// <returns>The validation result.</returns>
    public static EmbedAnalyticsValidationResult Validate(EmbedAnalyticsEvent analyticsEvent)
    {
        ArgumentNullException.ThrowIfNull(analyticsEvent);

        if (!Enum.IsDefined(analyticsEvent.EventType))
        {
            return EmbedAnalyticsValidationResult.Invalid("eventType is not a recognized value");
        }

        foreach (var (name, value) in EnumerateFields(analyticsEvent))
        {
            if (value is null)
            {
                continue;
            }

            if (value.Length > MaxFieldLength)
            {
                return EmbedAnalyticsValidationResult.Invalid($"{name} exceeds the maximum length of {MaxFieldLength}");
            }

            if (EmbedKeyMaterial.LooksLikeRawKey(value))
            {
                return EmbedAnalyticsValidationResult.Invalid(
                    $"{name} appears to contain raw embed key material; analytics events must be redacted");
            }
        }

        return EmbedAnalyticsValidationResult.Valid;
    }

    private static IEnumerable<(string Name, string? Value)> EnumerateFields(EmbedAnalyticsEvent analyticsEvent)
    {
        yield return (nameof(analyticsEvent.IntegrationId), analyticsEvent.IntegrationId);
        yield return (nameof(analyticsEvent.TenantId), analyticsEvent.TenantId);
        yield return (nameof(analyticsEvent.Origin), analyticsEvent.Origin);
        yield return (nameof(analyticsEvent.ServiceId), analyticsEvent.ServiceId);
        yield return (nameof(analyticsEvent.LayerId), analyticsEvent.LayerId);
    }
}
