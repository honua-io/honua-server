// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class DefaultAlertEvaluatorTests
{
    private static readonly WKBWriter WkbWriter = new();

    [UnitTest]
    public async Task EvaluateAsync_EnterRuleWhileStillInside_DoesNotEmitOngoingEnterEvent()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Enter);
        var zone = CreateZone();
        var feature = CreateFeature(100);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = evaluatedAt.AddMinutes(-5),
            LastAlertAt = evaluatedAt.AddMinutes(-2),
            LastGeneration = 1
        };

        var result = await evaluator.EvaluateAsync(
            new AlertChange
            {
                Generation = 2,
                LayerId = rule.LayerId,
                ObjectId = 100,
                Operation = AlertChangeOperation.Update,
                ChangedAt = evaluatedAt
            },
            feature,
            rule,
            zone,
            state,
            evaluatedAt);

        result.Events.Should().BeEmpty();
        result.UpdatedState.Should().NotBeNull();
        result.UpdatedState!.Inside.Should().BeTrue();
    }

    private static AlertRuleDefinition CreateRule(AlertTriggerType triggerType) =>
        new()
        {
            RuleId = 44,
            ServiceId = "svc-1",
            LayerId = 7,
            ZoneId = 9,
            RuleName = "zone-enter",
            TriggerType = triggerType,
            Severity = AlertSeverity.Warning,
            EditionRequired = AlertEdition.Pro,
            Channels = ImmutableArray<AlertChannelType>.Empty,
            IsActive = true
        };

    private static AlertZoneDefinition CreateZone()
    {
        var ring = new LinearRing(
        [
            new Coordinate(-157.88, 21.29),
            new Coordinate(-157.88, 21.31),
            new Coordinate(-157.85, 21.31),
            new Coordinate(-157.85, 21.29),
            new Coordinate(-157.88, 21.29)
        ]);
        var polygon = new Polygon(ring) { SRID = 4326 };

        return new AlertZoneDefinition
        {
            ZoneId = 9,
            ServiceId = "svc-1",
            ZoneName = "Harbor",
            Geometry = WkbWriter.Write(polygon),
            GeometrySrid = 4326,
            IsActive = true
        };
    }

    private static Feature CreateFeature(long objectId)
    {
        var point = new Point(-157.86, 21.30) { SRID = 4326 };
        return Feature.Create(objectId, WkbWriter.Write(point), ImmutableDictionary<string, object?>.Empty);
    }
}
