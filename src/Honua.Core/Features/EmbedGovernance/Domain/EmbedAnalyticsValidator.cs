// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

public sealed record EmbedAnalyticsValidationResult
{
    public required bool IsValid { get; init; }

    public string? Error { get; init; }

    public static EmbedAnalyticsValidationResult Valid { get; } = new() { IsValid = true };

    public static EmbedAnalyticsValidationResult Invalid(string error) => new()
    {
        IsValid = false,
        Error = error,
    };
}

public static class EmbedAnalyticsValidator
{
    private const int MaxFieldLength = 512;

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
