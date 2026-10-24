// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
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
    public async Task EvaluateAsync_CopiesExplicitFeatureEventLineageWithoutReplacement()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Threshold, conditionsJson: """{"field":"speed","operator":">","value":50}""");
        var change = CreateChange(rule, 100, evaluatedAt) with
        {
            SourceEventId = "event-exact",
            OperationInstanceId = "opinst-exact",
            CorrelationId = "corr-exact",
            AuditId = "audit-exact",
            ProposalId = "proposal-exact",
        };

        var result = await evaluator.EvaluateAsync(
            change, CreateFeatureWithAttributes(100, ("speed", 65.0)), rule, null, null, evaluatedAt);

        var alert = result.Events.Should().ContainSingle().Subject;
        alert.SourceEventId.Should().Be(change.SourceEventId);
        alert.OperationInstanceId.Should().Be(change.OperationInstanceId);
        alert.CorrelationId.Should().Be(change.CorrelationId);
        alert.AuditId.Should().Be(change.AuditId);
        alert.ProposalId.Should().Be(change.ProposalId);
        alert.PayloadJson.Should().Contain("\"sourceEventId\":\"event-exact\"");
    }

    [UnitTest]
    public async Task EvaluateAsync_OlderGeneration_DoesNotRewriteStateOrEmit()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Enter);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = false,
            LastGeneration = 50,
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 10),
            CreateFeature(100),
            rule,
            CreateZone(),
            state,
            evaluatedAt);

        result.Events.Should().BeEmpty();
        result.UpdatedState.Should().BeNull();
    }

    [UnitTest]
    public async Task EvaluateAsync_EqualGeneration_StillEvaluates()
    {
        var evaluator = new DefaultAlertEvaluator();
        var evaluatedAt = DateTimeOffset.UtcNow;
        var rule = CreateRule(AlertTriggerType.Enter);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = false,
            LastGeneration = 5,
        };

        var result = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, evaluatedAt, generation: 5),
            CreateFeature(100),
            rule,
            CreateZone(),
            state,
            evaluatedAt);

        result.Events.Should().ContainSingle();
        result.UpdatedState.Should().NotBeNull();
        result.UpdatedState!.LastGeneration.Should().Be(5);
    }

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

    [UnitTest]
    public async Task PersistedInstants_FollowTheSharedWallClock_WithoutSleeping()
    {
        var time = new ScriptedTimeProvider();
        var evaluator = new DefaultAlertEvaluator();
        var start = time.GetUtcNow();
        var threshold = CreateRule(
            AlertTriggerType.Threshold,
            """{"field":"value","operator":">","value":50}""",
            cooldownSeconds: 300);
        var above = Feature.Create(100, null, ImmutableDictionary<string, object?>.Empty.Add("value", 60L));
        var below = Feature.Create(100, null, ImmutableDictionary<string, object?>.Empty.Add("value", 10L));

        var started = await evaluator.EvaluateAsync(
            CreateChange(threshold, 100, start, 1), above, threshold, null, null, time.GetUtcNow());
        started.Events.Should().ContainSingle();
        started.UpdatedState!.LastAlertAt.Should().Be(start);

        time.Wall = time.Wall.AddHours(3);
        var jumped = time.GetUtcNow();
        jumped.Should().Be(start.AddHours(3));

        var resolved = await evaluator.EvaluateAsync(
            CreateChange(threshold, 100, jumped, 2), below, threshold, null, started.UpdatedState, jumped);
        resolved.Events.Should().ContainSingle();
        resolved.UpdatedState!.LastAlertAt.Should().Be(jumped);

        var otherReplica = new ScriptedTimeProvider();
        otherReplica.GetUtcNow().Should().Be(start);
    }

    [UnitTest]
    public async Task EvaluateAsync_CooldownSuppressedThreshold_ResolvesOnTheSameGenerationWhenDue()
    {
        var evaluator = new DefaultAlertEvaluator();
        var start = new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero);
        var rule = CreateRule(
            AlertTriggerType.Threshold,
            """{"field":"value","operator":">","value":50}""",
            cooldownSeconds: 300);
        var above = Feature.Create(100, null, ImmutableDictionary<string, object?>.Empty.Add("value", 60L));
        var below = Feature.Create(100, null, ImmutableDictionary<string, object?>.Empty.Add("value", 10L));

        var started = await evaluator.EvaluateAsync(CreateChange(rule, 100, start, 1), above, rule, null, null, start);
        var suppressed = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, start.AddMinutes(1), 2), below, rule, null, started.UpdatedState, start.AddMinutes(1));
        suppressed.Events.Should().BeEmpty();
        suppressed.UpdatedState!.ThresholdStateJson.Should().Contain("\"breached\":true");
        suppressed.UpdatedState.ThresholdStateJson.Should().Contain("pendingDueAt");
        suppressed.UpdatedState.LastGeneration.Should().Be(2);

        var sweepChange = CreateChange(rule, 100, start.AddMinutes(1), generation: 2);
        var tooEarly = await evaluator.EvaluateAsync(
            sweepChange, below, rule, null, suppressed.UpdatedState, start.AddMinutes(4));
        tooEarly.Events.Should().BeEmpty();
        tooEarly.UpdatedState!.ThresholdStateJson.Should().Contain("\"breached\":true");

        var due = await evaluator.EvaluateAsync(
            sweepChange, below, rule, null, tooEarly.UpdatedState, start.AddMinutes(6));
        due.Events.Should().ContainSingle();
        due.Events[0].PayloadJson.Should().Contain("\"transition\":\"threshold_resolved\"");
        due.UpdatedState!.ThresholdStateJson.Should().Contain("\"breached\":false");
        due.UpdatedState.ThresholdStateJson.Should().NotContain("pendingDueAt");
        due.UpdatedState.LastGeneration.Should().Be(2);
    }

    [UnitTest]
    public async Task EvaluateAsync_CooldownSuppressedExit_StaysPendingUntilDue_WithoutANewGeneration()
    {
        var evaluator = new DefaultAlertEvaluator();
        var start = new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero);
        var rule = CreateRule(AlertTriggerType.Exit, cooldownSeconds: 300);
        var zone = CreateZone();
        var inside = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = start,
            LastAlertAt = start,
            LastGeneration = 1
        };

        var suppressed = await evaluator.EvaluateAsync(
            CreateChange(rule, 100, start.AddMinutes(1), 2),
            CreateFeatureOutsideZone(100),
            rule,
            zone,
            inside,
            start.AddMinutes(1));
        suppressed.Events.Should().BeEmpty();
        suppressed.UpdatedState!.Inside.Should().BeTrue();
        suppressed.UpdatedState.ThresholdStateJson.Should().Contain("\"pendingEdge\":\"exit\"");
        suppressed.UpdatedState.LastGeneration.Should().Be(2);

        var sweepChange = CreateChange(rule, 100, start.AddMinutes(1), generation: 2);
        var tooEarly = await evaluator.EvaluateAsync(
            sweepChange, CreateFeatureOutsideZone(100), rule, zone, suppressed.UpdatedState, start.AddMinutes(4));
        tooEarly.Events.Should().BeEmpty();
        tooEarly.UpdatedState!.Inside.Should().BeTrue();

        var due = await evaluator.EvaluateAsync(
            sweepChange, CreateFeatureOutsideZone(100), rule, zone, tooEarly.UpdatedState, start.AddMinutes(6));
        due.Events.Should().ContainSingle();
        due.Events[0].PayloadJson.Should().Contain("\"transition\":\"exit\"");
        due.UpdatedState!.Inside.Should().BeFalse();
        due.UpdatedState.ThresholdStateJson.Should().NotContain("pendingDueAt");
    }

    [UnitTest]
    public void Fit_OmitsGeometryThatExceedsTheSelectedChannelLimit_AndKeepsSmallGeometry()
    {
        var tiny = EventWithGeometry(new byte[] { 1, 2, 3, 4 });
        var fittedTiny = AlertPayloadBounds.Fit(tiny, ImmutableArray.Create(AlertChannelType.AwsSns));
        fittedTiny.PayloadJson.Should().Contain("geometryWkbHex");
        fittedTiny.PayloadJson.Should().NotContain("geometryOmitted");

        var large = EventWithGeometry(new byte[5 * 1024 * 1024]);
        var fitted = AlertPayloadBounds.Fit(large, ImmutableArray.Create(AlertChannelType.AwsSns));
        Encoding.UTF8.GetByteCount(fitted.PayloadJson).Should().BeLessThanOrEqualTo(AlertPayloadBounds.SnsLimitBytes);
        fitted.PayloadJson.Should().Contain("\"geometryOmitted\":true");
        fitted.PayloadJson.Should().Contain("geometryRef");
        fitted.PayloadJson.Should().NotContain("geometryWkbHex");
        fitted.DedupeKey.Should().Be(large.DedupeKey);

        var webhook = AlertPayloadBounds.Fit(large, ImmutableArray.Create(AlertChannelType.Webhook));
        webhook.PayloadJson.Should().Contain("\"geometryOmitted\":true");
        webhook.PayloadJson.Should().NotContain("geometryWkbHex");
    }

    private static AlertEventEnvelope EventWithGeometry(byte[] geometry)
    {
        var payload = new JsonObject
        {
            ["serviceId"] = "svc-1",
            ["layerId"] = 7,
            ["objectId"] = 100,
            ["ruleId"] = 44,
            ["generation"] = 3,
            ["geometryWkbHex"] = Convert.ToHexString(geometry),
            ["attributes"] = new JsonObject { ["value"] = 60 }
        };
        return new AlertEventEnvelope
        {
            DedupeKey = "dedupe",
            RuleId = 44,
            ServiceId = "svc-1",
            LayerId = 7,
            ObjectId = 100,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 3,
            Severity = AlertSeverity.Warning,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = payload.ToJsonString()
        };
    }

    [UnitTest]
    public async Task EvaluateAsync_CooldownDwellAndOffsets_UseMonotonicInstants()
    {
        var evaluator = new DefaultAlertEvaluator();
        var start = new DateTimeOffset(2026, 3, 8, 10, 30, 0, TimeSpan.Zero);
        var threshold = CreateRule(
            AlertTriggerType.Threshold,
            """{"field":"value","operator":">","value":50}""",
            cooldownSeconds: 300);
        var above = Feature.Create(100, null, ImmutableDictionary<string, object?>.Empty.Add("value", 60L));
        var below = Feature.Create(100, null, ImmutableDictionary<string, object?>.Empty.Add("value", 10L));

        var started = await evaluator.EvaluateAsync(CreateChange(threshold, 100, start, 1), above, threshold, null, null, start);
        started.Events.Should().ContainSingle();
        started.Events[0].IncidentStatus.Should().Be(AlertIncidentStatus.Started);

        var early = await evaluator.EvaluateAsync(
            CreateChange(threshold, 100, start.AddMinutes(4), 2), below, threshold, null, started.UpdatedState, start.AddMinutes(4));
        early.Events.Should().BeEmpty();
        early.UpdatedState!.ThresholdStateJson.Should().Contain("\"breached\":true");
        early.UpdatedState.ThresholdStateJson.Should().Contain("pendingDueAt");

        var resolved = await evaluator.EvaluateAsync(
            CreateChange(threshold, 100, start.AddMinutes(5), 3), below, threshold, null, early.UpdatedState, start.AddMinutes(5));
        resolved.Events.Should().ContainSingle();
        resolved.Events[0].PayloadJson.Should().Contain("\"transition\":\"threshold_resolved\"");
        resolved.Events[0].IncidentDurationMs.Should().BeGreaterThanOrEqualTo(0);
        resolved.UpdatedState!.ThresholdStateJson.Should().NotContain("pendingDueAt");

        var openCooldown = CreateRule(AlertTriggerType.Threshold, """{"field":"value","operator":">","value":50}""");
        var backward = await evaluator.EvaluateAsync(
            CreateChange(openCooldown, 100, start, 4),
            below,
            openCooldown,
            null,
            new AlertStateSnapshot
            {
                RuleId = openCooldown.RuleId,
                LayerId = openCooldown.LayerId,
                ObjectId = 100,
                Inside = false,
                LastGeneration = 1,
                ThresholdStateJson = "{\"breached\":true,\"breachedSince\":\"2026-03-08T10:30:00.0000000+00:00\"}"
            },
            start.AddMinutes(-10));
        backward.Events.Should().ContainSingle();
        backward.Events[0].IncidentDurationMs.Should().Be(0);

        var pacific = new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-7));
        var utcEvent = await evaluator.EvaluateAsync(CreateChange(threshold, 100, start, 9), above, threshold, null, null, start);
        var offsetEvent = await evaluator.EvaluateAsync(CreateChange(threshold, 100, pacific, 9), above, threshold, null, null, start);
        offsetEvent.Events[0].DedupeKey.Should().Be(utcEvent.Events[0].DedupeKey);
        offsetEvent.Events[0].PayloadJson.Should().Be(utcEvent.Events[0].PayloadJson);

        var dwell = CreateRule(AlertTriggerType.Dwell, """{"dwellSeconds":300}""", cooldownSeconds: 300);
        var zone = CreateZone();
        var inside = CreateFeature(100);
        var dwelling = new AlertStateSnapshot
        {
            RuleId = dwell.RuleId,
            LayerId = dwell.LayerId,
            ObjectId = 100,
            Inside = true,
            EnteredAt = start,
            LastGeneration = 1
        };
        var beforeDwell = await evaluator.EvaluateAsync(
            CreateChange(dwell, 100, start.AddMinutes(4), 5), inside, dwell, zone, dwelling, start.AddMinutes(4));
        beforeDwell.Events.Should().BeEmpty();
        var atDwell = await evaluator.EvaluateAsync(
            CreateChange(dwell, 100, start.AddMinutes(5), 6), inside, dwell, zone, beforeDwell.UpdatedState, start.AddMinutes(5));
        atDwell.Events.Should().ContainSingle();
        var again = await evaluator.EvaluateAsync(
            CreateChange(dwell, 100, start.AddMinutes(5), 7), inside, dwell, zone, atDwell.UpdatedState, start.AddMinutes(5));
        again.Events.Should().BeEmpty();
    }

    private sealed class ScriptedTimeProvider : TimeProvider
    {
        public DateTimeOffset Wall { get; set; } = new(2026, 3, 8, 10, 30, 0, TimeSpan.Zero);

        public TimeSpan Elapsed { get; set; }

        public override DateTimeOffset GetUtcNow() => Wall;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => TimeSpan.TicksPerSecond + Elapsed.Ticks;
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
