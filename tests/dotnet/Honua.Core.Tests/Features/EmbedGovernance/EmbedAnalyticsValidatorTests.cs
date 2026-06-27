// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EmbedGovernance.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.EmbedGovernance;

/// <summary>
/// Unit tests for <see cref="EmbedAnalyticsValidator"/> and raw-key detection.
/// </summary>
public sealed class EmbedAnalyticsValidatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 26, 0, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void Validate_RedactedEvent_IsValid()
    {
        var analyticsEvent = new EmbedAnalyticsEvent
        {
            EventType = EmbedAnalyticsEventType.View,
            IntegrationId = "site-7",
            Origin = "https://app.example.com",
            ServiceId = "services/Roads",
            LayerId = "0",
            OccurredAt = _now,
        };

        var result = EmbedAnalyticsValidator.Validate(analyticsEvent);

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_FieldContainingRawKey_IsRejected()
    {
        var analyticsEvent = new EmbedAnalyticsEvent
        {
            EventType = EmbedAnalyticsEventType.Search,
            IntegrationId = $"{EmbedKeyMaterial.Prefix}abc123leaked",
            OccurredAt = _now,
        };

        var result = EmbedAnalyticsValidator.Validate(analyticsEvent);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("raw embed key");
    }

    [UnitTest]
    public void Validate_RawKeyEmbeddedInUrl_IsRejected()
    {
        var analyticsEvent = new EmbedAnalyticsEvent
        {
            EventType = EmbedAnalyticsEventType.View,
            Origin = $"https://app.example.com/?key={EmbedKeyMaterial.Prefix}leak",
            OccurredAt = _now,
        };

        var result = EmbedAnalyticsValidator.Validate(analyticsEvent);

        result.IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void Validate_OverlongField_IsRejected()
    {
        var analyticsEvent = new EmbedAnalyticsEvent
        {
            EventType = EmbedAnalyticsEventType.View,
            LayerId = new string('x', 1000),
            OccurredAt = _now,
        };

        var result = EmbedAnalyticsValidator.Validate(analyticsEvent);

        result.IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void Validate_UndefinedEventType_IsRejected()
    {
        var analyticsEvent = new EmbedAnalyticsEvent
        {
            EventType = (EmbedAnalyticsEventType)99,
            OccurredAt = _now,
        };

        var result = EmbedAnalyticsValidator.Validate(analyticsEvent);

        result.IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void LooksLikeRawKey_DetectsPrefixedValues()
    {
        EmbedKeyMaterial.LooksLikeRawKey(EmbedKeyMaterial.Generate()).Should().BeTrue();
        EmbedKeyMaterial.LooksLikeRawKey("integration-7").Should().BeFalse();
        EmbedKeyMaterial.LooksLikeRawKey(null).Should().BeFalse();
    }
}
