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

    // ---- Threshold trigger: fires only on the below->above (Started) and
    // above->below (Ended/"threshold_resolved") transition edges; re-evaluating
    // while already breached must NOT re-fire (honua-server#2945).

    [UnitTest]
    public async Task EvaluateAsync_ThresholdRule_BelowToAboveTransition_EmitsStartedEvent()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Threshold, conditionsJson: """{"field":"speed","operator":">","value":50}""");
        var feature = CreateFeatureWithAttributes(100, ("speed", 65.0));

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt),
            feature,
            rule,
            zone: null,
            currentState: null,
            evaluatedAt);

        result.Events.Should().ContainSingle();
        var evt = result.Events[0];
        evt.TriggerType.Should().Be(AlertTriggerType.Threshold);
        evt.IncidentStatus.Should().Be(AlertIncidentStatus.Started);
        evt.IncidentDurationMs.Should().Be(0);
        evt.PayloadJson.Should().Contain("\"transition\":\"threshold\"");
        result.UpdatedState!.ThresholdStateJson.Should().Contain("\"breached\":true");
    }

    [UnitTest]
    public async Task EvaluateAsync_ThresholdRule_StaysAboveOnSubsequentEvaluation_DoesNotReFire()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Threshold, conditionsJson: """{"field":"speed","operator":">","value":50}""");
        var feature = CreateFeatureWithAttributes(100, ("speed", 70.0));
        var breachedSince = evaluatedAt.AddMinutes(-10);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = false,
            LastAlertAt = breachedSince,
            LastGeneration = 1,
            ThresholdStateJson = $$"""{"breached":true,"breachedSince":"{{breachedSince:O}}"}"""
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 2),
            feature,
            rule,
            zone: null,
            state,
            evaluatedAt);

        result.Events.Should().BeEmpty("a rule already breached must not re-fire on every evaluation");
        result.UpdatedState!.ThresholdStateJson.Should().Contain("\"breached\":true");
    }

    [UnitTest]
    public async Task EvaluateAsync_ThresholdRule_AboveToBelowTransition_EmitsResolvedEventWithDuration()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Threshold, conditionsJson: """{"field":"speed","operator":">","value":50}""");
        var feature = CreateFeatureWithAttributes(100, ("speed", 10.0));
        var breachedSince = evaluatedAt.AddMinutes(-10);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = false,
            LastAlertAt = breachedSince,
            LastGeneration = 1,
            ThresholdStateJson = $$"""{"breached":true,"breachedSince":"{{breachedSince:O}}"}"""
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 2),
            feature,
            rule,
            zone: null,
            state,
            evaluatedAt);

        result.Events.Should().ContainSingle();
        var evt = result.Events[0];
        evt.IncidentStatus.Should().Be(AlertIncidentStatus.Ended);
        evt.PayloadJson.Should().Contain("\"transition\":\"threshold_resolved\"");
        ((double)evt.IncidentDurationMs).Should().BeApproximately(
            (evaluatedAt - breachedSince).TotalMilliseconds, 1000);
        result.UpdatedState!.ThresholdStateJson.Should().Contain("\"breached\":false");
    }

    // ---- Dwell trigger: fires once elapsed time inside >= dwellSeconds, and
    // (unlike Threshold) keeps firing on every subsequent evaluation once the
    // cooldown elapses (an "Ongoing" incident, not a single edge).

    [UnitTest]
    public async Task EvaluateAsync_DwellRule_InsideBelowDwellDuration_DoesNotEmitEvent()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Dwell, conditionsJson: """{"dwellSeconds":300}""");
        var zone = CreateZone();
        var feature = CreateFeature(100);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = evaluatedAt.AddSeconds(-100), // only 100s inside, dwell requires 300s
            LastGeneration = 1
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 2),
            feature,
            rule,
            zone,
            state,
            evaluatedAt);

        result.Events.Should().BeEmpty();
        result.UpdatedState!.Inside.Should().BeTrue();
    }

    [UnitTest]
    public async Task EvaluateAsync_DwellRule_InsideAtOrAboveDwellDuration_EmitsStartedEvent()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Dwell, conditionsJson: """{"dwellSeconds":300}""");
        var zone = CreateZone();
        var feature = CreateFeature(100);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = evaluatedAt.AddSeconds(-300), // exactly at dwell duration
            LastGeneration = 1
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 2),
            feature,
            rule,
            zone,
            state,
            evaluatedAt);

        result.Events.Should().ContainSingle();
        var evt = result.Events[0];
        evt.TriggerType.Should().Be(AlertTriggerType.Dwell);
        evt.IncidentStatus.Should().Be(AlertIncidentStatus.Started, "no prior alert has been emitted for this incident yet");
        evt.PayloadJson.Should().Contain("\"transition\":\"dwell\"");
    }

    [UnitTest]
    public async Task EvaluateAsync_DwellRule_ReEvaluatedAfterFirstAlert_EmitsOngoingEvent()
    {
        // Dwell (unlike Threshold) is not edge-triggered: once past the cooldown it
        // re-fires on every subsequent evaluation while still inside, reported as
        // "Ongoing" rather than "Started".
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Dwell, conditionsJson: """{"dwellSeconds":300}""", cooldownSeconds: 60);
        var zone = CreateZone();
        var feature = CreateFeature(100);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = evaluatedAt.AddSeconds(-600),
            LastAlertAt = evaluatedAt.AddSeconds(-120), // outside the 60s cooldown
            LastGeneration = 1
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 2),
            feature,
            rule,
            zone,
            state,
            evaluatedAt);

        result.Events.Should().ContainSingle();
        result.Events[0].IncidentStatus.Should().Be(AlertIncidentStatus.Ongoing);
    }

    // ---- Exit trigger: fires only on the inside->outside transition, including
    // when the transition is caused by a delete of a previously-inside feature.

    [UnitTest]
    public async Task EvaluateAsync_ExitRule_MovesOutsideZone_EmitsExitEventWithDuration()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Exit);
        var zone = CreateZone();
        var enteredAt = evaluatedAt.AddMinutes(-15);
        var feature = CreateFeatureOutsideZone(100);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = enteredAt,
            LastGeneration = 1
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 2),
            feature,
            rule,
            zone,
            state,
            evaluatedAt);

        result.Events.Should().ContainSingle();
        var evt = result.Events[0];
        evt.TriggerType.Should().Be(AlertTriggerType.Exit);
        evt.IncidentStatus.Should().Be(AlertIncidentStatus.Ended);
        ((double)evt.IncidentDurationMs).Should().BeApproximately((evaluatedAt - enteredAt).TotalMilliseconds, 1000);
        result.UpdatedState!.Inside.Should().BeFalse();
    }

    [UnitTest]
    public async Task EvaluateAsync_ExitRule_DeleteWhilePreviouslyInside_EmitsExitEvent()
    {
        // A delete of a previously-tracked feature is itself an "inside -> outside"
        // transition (DetermineInside treats Delete as never-inside).
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Exit);
        var zone = CreateZone();
        var enteredAt = evaluatedAt.AddMinutes(-5);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = enteredAt,
            LastGeneration = 1
        };

        var result = await evaluator.EvaluateAsync(
            new AlertChange
            {
                Generation = 2,
                LayerId = rule.LayerId,
                ObjectId = 100,
                Operation = AlertChangeOperation.Delete,
                ChangedAt = evaluatedAt
            },
            feature: null,
            rule,
            zone,
            state,
            evaluatedAt);

        result.Events.Should().ContainSingle();
        result.Events[0].IncidentStatus.Should().Be(AlertIncidentStatus.Ended);
        result.UpdatedState!.Inside.Should().BeFalse();
    }

    [UnitTest]
    public async Task EvaluateAsync_ExitRule_StaysInside_DoesNotEmitEvent()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Exit);
        var zone = CreateZone();
        var feature = CreateFeature(100);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = evaluatedAt.AddMinutes(-5),
            LastGeneration = 1
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 2),
            feature,
            rule,
            zone,
            state,
            evaluatedAt);

        result.Events.Should().BeEmpty();
        result.UpdatedState!.Inside.Should().BeTrue();
    }

    private static AlertChange CreateChange(
        AlertRuleDefinition rule, long objectId, DateTimeOffset changedAt, long generation = 1) =>
        new()
        {
            Generation = generation,
            LayerId = rule.LayerId,
            ObjectId = objectId,
            Operation = AlertChangeOperation.Update,
            ChangedAt = changedAt
        };

    private static AlertRuleDefinition CreateRule(
        AlertTriggerType triggerType,
        string conditionsJson = "{}",
        int cooldownSeconds = 0) =>
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
            IsActive = true,
            ConditionsJson = conditionsJson,
            CooldownSeconds = cooldownSeconds
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

    private static Feature CreateFeatureOutsideZone(long objectId)
    {
        var point = new Point(-150.00, 10.00) { SRID = 4326 };
        return Feature.Create(objectId, WkbWriter.Write(point), ImmutableDictionary<string, object?>.Empty);
    }

    private static Feature CreateFeatureWithAttributes(long objectId, params (string Name, object? Value)[] attributes)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>();
        foreach (var (name, value) in attributes)
        {
            builder[name] = value;
        }

        var point = new Point(-157.86, 21.30) { SRID = 4326 };
        return Feature.Create(objectId, WkbWriter.Write(point), builder.ToImmutable());
    }
}
