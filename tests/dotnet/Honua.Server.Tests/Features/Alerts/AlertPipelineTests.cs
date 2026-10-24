// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.Server.Tests.Infrastructure.Telemetry;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertPipelineTests
{
    [UnitTest]
    public async Task ProcessChangesAsync_BatchesRuleZoneAndStateLookups()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var graphProvider = CreateGraphProviderWithService("svc-7", layerId: 7);
        var evaluator = Substitute.For<IAlertEvaluator>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var changes = new[]
        {
            new AlertChange { Generation = 1, LayerId = 7, ObjectId = 100, Operation = AlertChangeOperation.Update, ChangedAt = DateTimeOffset.UtcNow },
            new AlertChange { Generation = 2, LayerId = 7, ObjectId = 101, Operation = AlertChangeOperation.Update, ChangedAt = DateTimeOffset.UtcNow }
        };
        var rule = CreateRule();
        const string serviceName = "svc-7";
        var zone = CreateZone(rule.ZoneId!.Value);

        changeReader.GetChangesAfterAsync(0, 10, Arg.Any<CancellationToken>())
            .Returns(changes);
        ruleRepository.GetActiveRulesAsync(Arg.Any<IReadOnlyCollection<AlertRuleLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>
            {
                [new AlertRuleLookupKey(serviceName, rule.LayerId)] = [rule]
            });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition> { [zone.ZoneId] = zone });
        stateStore.GetManyAsync(Arg.Any<IReadOnlyCollection<AlertStateLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertStateLookupKey, AlertStateSnapshot>());
        featureReader.GetAsync(rule.LayerId, 100, Arg.Any<CancellationToken>())
            .Returns(CreateFeature(100));
        featureReader.GetAsync(rule.LayerId, 101, Arg.Any<CancellationToken>())
            .Returns(CreateFeature(101));
        editionPolicy.IsRuleAllowed(rule).Returns(true);
        evaluator.EvaluateAsync(Arg.Any<AlertChange>(), Arg.Any<Feature?>(), rule, zone, Arg.Any<AlertStateSnapshot?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AlertEvaluationResult());

        var dispatchWriter = new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance);
        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            dispatchWriter,
            NullLogger<AlertPipeline>.Instance);

        var maxGeneration = await sut.ProcessChangesAsync(0, 10, cancellationToken: CancellationToken.None);

        maxGeneration.Should().Be(2);
        await ruleRepository.Received(1).GetActiveRulesAsync(
            Arg.Is<IReadOnlyCollection<AlertRuleLookupKey>>(keys =>
                keys.Count == 1 &&
                keys.Single() == new AlertRuleLookupKey(serviceName, rule.LayerId)),
            Arg.Any<CancellationToken>());
        await stateStore.Received(1).GetManyAsync(
            Arg.Is<IReadOnlyCollection<AlertStateLookupKey>>(keys =>
                keys.Count == 2 &&
                keys.Contains(new AlertStateLookupKey(rule.RuleId, rule.LayerId, 100)) &&
                keys.Contains(new AlertStateLookupKey(rule.RuleId, rule.LayerId, 101))),
            Arg.Any<CancellationToken>());
        await ruleRepository.Received(1).GetZonesAsync(
            Arg.Is<IReadOnlyCollection<long>>(keys => keys.Count == 1 && keys.Contains(zone.ZoneId)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SweepDwellAsync_BatchesRuleAndZoneLookups()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var graphProvider = CreateGraphProviderWithService("svc-7", layerId: 7);
        var evaluator = Substitute.For<IAlertEvaluator>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var rule = CreateRule(triggerType: AlertTriggerType.Dwell);
        var zone = CreateZone(rule.ZoneId!.Value);
        var dueStates = new[]
        {
            new AlertStateSnapshot { RuleId = rule.RuleId, LayerId = rule.LayerId, ObjectId = 100, Inside = true, EnteredAt = DateTimeOffset.UtcNow.AddMinutes(-5), LastGeneration = 1 },
            new AlertStateSnapshot { RuleId = rule.RuleId, LayerId = rule.LayerId, ObjectId = 101, Inside = true, EnteredAt = DateTimeOffset.UtcNow.AddMinutes(-5), LastGeneration = 2 }
        };

        stateStore.GetDwellCandidatesAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>())
            .Returns(dueStates);
        ruleRepository.GetRulesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertRuleDefinition> { [rule.RuleId] = rule });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition> { [zone.ZoneId] = zone });
        featureReader.GetAsync(rule.LayerId, 100, Arg.Any<CancellationToken>())
            .Returns(CreateFeature(100));
        featureReader.GetAsync(rule.LayerId, 101, Arg.Any<CancellationToken>())
            .Returns(CreateFeature(101));
        editionPolicy.IsRuleAllowed(rule).Returns(true);
        evaluator.EvaluateAsync(Arg.Any<AlertChange>(), Arg.Any<Feature?>(), rule, zone, Arg.Any<AlertStateSnapshot?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AlertEvaluationResult());

        var dispatchWriter = new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance);
        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            dispatchWriter,
            NullLogger<AlertPipeline>.Instance);

        var evaluated = await sut.SweepDwellAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);

        evaluated.Should().Be(2);
        await ruleRepository.Received(1).GetRulesAsync(
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.Count == 1 && ids.Contains(rule.RuleId)),
            Arg.Any<CancellationToken>());
        await ruleRepository.Received(1).GetZonesAsync(
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.Count == 1 && ids.Contains(zone.ZoneId)),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ProcessChangesAsync_BatchesStateUpsertsPerChange()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var graphProvider = CreateGraphProviderWithService("svc-7", layerId: 7);
        var evaluator = Substitute.For<IAlertEvaluator>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var ruleOne = CreateRule();
        var ruleTwo = CreateRule();
        const string serviceName = "svc-7";
        var zone = CreateZone(ruleOne.ZoneId!.Value);
        var change = new AlertChange
        {
            Generation = 1,
            LayerId = ruleOne.LayerId,
            ObjectId = 100,
            Operation = AlertChangeOperation.Update,
            ChangedAt = DateTimeOffset.UtcNow
        };

        ruleTwo = ruleTwo with { RuleId = 100 };

        changeReader.GetChangesAfterAsync(0, 10, Arg.Any<CancellationToken>())
            .Returns([change]);
        ruleRepository.GetActiveRulesAsync(Arg.Any<IReadOnlyCollection<AlertRuleLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>
            {
                [new AlertRuleLookupKey(serviceName, ruleOne.LayerId)] = [ruleOne, ruleTwo]
            });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition> { [zone.ZoneId] = zone });
        stateStore.GetManyAsync(Arg.Any<IReadOnlyCollection<AlertStateLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertStateLookupKey, AlertStateSnapshot>());
        featureReader.GetAsync(ruleOne.LayerId, change.ObjectId, Arg.Any<CancellationToken>())
            .Returns(CreateFeature(change.ObjectId));
        editionPolicy.IsRuleAllowed(Arg.Any<AlertRuleDefinition>()).Returns(true);
        evaluator.EvaluateAsync(change, Arg.Any<Feature?>(), ruleOne, zone, null, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AlertEvaluationResult
            {
                UpdatedState = CreateState(ruleOne.RuleId, ruleOne.LayerId, change.ObjectId, change.Generation)
            });
        evaluator.EvaluateAsync(change, Arg.Any<Feature?>(), ruleTwo, zone, null, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AlertEvaluationResult
            {
                UpdatedState = CreateState(ruleTwo.RuleId, ruleTwo.LayerId, change.ObjectId, change.Generation)
            });

        var dispatchWriter = new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance);
        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            dispatchWriter,
            NullLogger<AlertPipeline>.Instance);

        _ = await sut.ProcessChangesAsync(0, 10, cancellationToken: CancellationToken.None);

        await outbox.Received(1).CommitEvaluationAsync(
            Arg.Is<IReadOnlyCollection<AlertStateSnapshot>>(states =>
                states.Count == 2 &&
                states.Any(state => state.RuleId == ruleOne.RuleId) &&
                states.Any(state => state.RuleId == ruleTwo.RuleId)),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Count == 0),
            Arg.Any<CancellationToken>());
        await stateStore.DidNotReceive().UpsertManyAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SweepDwellAsync_BatchesStateUpsertsAcrossDueStates()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var graphProvider = CreateGraphProviderWithService("svc-7", layerId: 7);
        var evaluator = Substitute.For<IAlertEvaluator>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var rule = CreateRule(triggerType: AlertTriggerType.Dwell);
        var zone = CreateZone(rule.ZoneId!.Value);
        var dueStates = new[]
        {
            CreateState(rule.RuleId, rule.LayerId, 100, 1) with { Inside = true, EnteredAt = DateTimeOffset.UtcNow.AddMinutes(-10) },
            CreateState(rule.RuleId, rule.LayerId, 101, 2) with { Inside = true, EnteredAt = DateTimeOffset.UtcNow.AddMinutes(-5) }
        };

        stateStore.GetDwellCandidatesAsync(Arg.Any<DateTimeOffset>(), 10, Arg.Any<CancellationToken>())
            .Returns(dueStates);
        ruleRepository.GetRulesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertRuleDefinition> { [rule.RuleId] = rule });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition> { [zone.ZoneId] = zone });
        featureReader.GetAsync(rule.LayerId, 100, Arg.Any<CancellationToken>())
            .Returns(CreateFeature(100));
        featureReader.GetAsync(rule.LayerId, 101, Arg.Any<CancellationToken>())
            .Returns(CreateFeature(101));
        editionPolicy.IsRuleAllowed(rule).Returns(true);
        evaluator.EvaluateAsync(Arg.Any<AlertChange>(), Arg.Any<Feature?>(), rule, zone, Arg.Any<AlertStateSnapshot?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(
                new AlertEvaluationResult { UpdatedState = CreateState(rule.RuleId, rule.LayerId, 100, 1) },
                new AlertEvaluationResult { UpdatedState = CreateState(rule.RuleId, rule.LayerId, 101, 2) });

        var dispatchWriter = new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance);
        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            dispatchWriter,
            NullLogger<AlertPipeline>.Instance);

        _ = await sut.SweepDwellAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);

        await outbox.Received(1).CommitEvaluationAsync(
            Arg.Is<IReadOnlyCollection<AlertStateSnapshot>>(states =>
                states.Count == 2 &&
                states.Any(state => state.ObjectId == 100L) &&
                states.Any(state => state.ObjectId == 101L)),
            Arg.Is<IReadOnlyList<AlertOutboxEntry>>(entries => entries.Count == 0),
            Arg.Any<CancellationToken>());
        await stateStore.DidNotReceive().UpsertManyAsync(Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ProcessChangesAsync_ChangeAtOrBehindStoredGeneration_DoesNotReevaluate()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var graphProvider = CreateGraphProviderWithService("svc-7", layerId: 7);
        var evaluator = Substitute.For<IAlertEvaluator>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var rule = CreateRule();
        const string serviceName = "svc-7";
        var zone = CreateZone(rule.ZoneId!.Value);
        var change = new AlertChange
        {
            Generation = 4,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Operation = AlertChangeOperation.Update,
            ChangedAt = DateTimeOffset.UtcNow,
        };

        changeReader.GetChangesAfterAsync(0, 10, Arg.Any<CancellationToken>()).Returns([change]);
        ruleRepository.GetActiveRulesAsync(Arg.Any<IReadOnlyCollection<AlertRuleLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>
            {
                [new AlertRuleLookupKey(serviceName, rule.LayerId)] = [rule],
            });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition> { [zone.ZoneId] = zone });
        stateStore.GetManyAsync(Arg.Any<IReadOnlyCollection<AlertStateLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertStateLookupKey, AlertStateSnapshot>
            {
                [new AlertStateLookupKey(rule.RuleId, rule.LayerId, 100)] = CreateState(rule.RuleId, rule.LayerId, 100, 4),
            });
        featureReader.GetAsync(rule.LayerId, 100, Arg.Any<CancellationToken>()).Returns(CreateFeature(100));
        editionPolicy.IsRuleAllowed(rule).Returns(true);

        var dispatchWriter = new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance);
        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            dispatchWriter,
            NullLogger<AlertPipeline>.Instance);

        var maxGeneration = await sut.ProcessChangesAsync(0, 10, cancellationToken: CancellationToken.None);

        maxGeneration.Should().Be(4);
        await evaluator.DidNotReceive().EvaluateAsync(
            Arg.Any<AlertChange>(),
            Arg.Any<Feature?>(),
            Arg.Any<AlertRuleDefinition>(),
            Arg.Any<AlertZoneDefinition?>(),
            Arg.Any<AlertStateSnapshot?>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().CommitEvaluationAsync(
            Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
            Arg.Any<IReadOnlyList<AlertOutboxEntry>>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ProcessChangesAsync_CommittedImages_AreNotReplacedByTheLiveRow()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var graphProvider = CreateGraphProviderWithService("svc-7", layerId: 7);
        var evaluator = Substitute.For<IAlertEvaluator>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var rule = CreateRule(AlertTriggerType.Threshold);
        const string serviceName = "svc-7";
        var observed = new List<long?>();
        var changes = new[]
        {
            CommittedChange(1, 10),
            CommittedChange(2, 60),
            CommittedChange(3, 10),
        };

        changeReader.GetChangesAfterAsync(0, 10, Arg.Any<CancellationToken>()).Returns(changes);
        ruleRepository.GetActiveRulesAsync(Arg.Any<IReadOnlyCollection<AlertRuleLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>
            {
                [new AlertRuleLookupKey(serviceName, rule.LayerId)] = [rule]
            });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition>());
        stateStore.GetManyAsync(Arg.Any<IReadOnlyCollection<AlertStateLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertStateLookupKey, AlertStateSnapshot>());
        featureReader.GetAsync(rule.LayerId, 100, Arg.Any<CancellationToken>())
            .Returns(FeatureWithValue(100, 1));
        editionPolicy.IsRuleAllowed(rule).Returns(true);
        evaluator.EvaluateAsync(
                Arg.Any<AlertChange>(),
                Arg.Any<Feature?>(),
                rule,
                Arg.Any<AlertZoneDefinition?>(),
                Arg.Any<AlertStateSnapshot?>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var feature = call.ArgAt<Feature?>(1);
                observed.Add(feature.HasValue && feature.Value.Attributes.TryGetValue("value", out var value) ? value as long? : null);
                return new AlertEvaluationResult();
            });

        var dispatchWriter = new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance);
        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            dispatchWriter,
            NullLogger<AlertPipeline>.Instance);

        var maxGeneration = await sut.ProcessChangesAsync(0, 10, cancellationToken: CancellationToken.None);

        maxGeneration.Should().Be(3);
        observed.Should().Equal(10L, 60L, 10L);
        await featureReader.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ProcessChangesAsync_ProjectsCommittedSnapshotsBeforeEvaluation()
    {
        var raw = Feature.Create(
            100,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("value", 60L).Add("secret", "leak"));
        var reader = new ProjectingChangeReader(new AlertChange
        {
            Generation = 4,
            LayerId = 7,
            ObjectId = 100,
            Operation = AlertChangeOperation.Update,
            ChangedAt = DateTimeOffset.UnixEpoch,
            HasCommittedImage = true,
            CommittedFeature = raw
        });
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var evaluator = Substitute.For<IAlertEvaluator>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var rule = CreateRule(AlertTriggerType.Threshold);
        var observed = new List<bool>();

        ruleRepository.GetActiveRulesAsync(Arg.Any<IReadOnlyCollection<AlertRuleLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>
            {
                [new AlertRuleLookupKey("svc-7", rule.LayerId)] = [rule]
            });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition>());
        stateStore.GetManyAsync(Arg.Any<IReadOnlyCollection<AlertStateLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertStateLookupKey, AlertStateSnapshot>());
        editionPolicy.IsRuleAllowed(rule).Returns(true);
        evaluator.EvaluateAsync(
                Arg.Any<AlertChange>(),
                Arg.Any<Feature?>(),
                rule,
                Arg.Any<AlertZoneDefinition?>(),
                Arg.Any<AlertStateSnapshot?>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var feature = call.ArgAt<Feature?>(1);
                observed.Add(feature.HasValue && feature.Value.Attributes.ContainsKey("secret"));
                return new AlertEvaluationResult();
            });

        var sut = new AlertPipeline(
            reader,
            ruleRepository,
            stateStore,
            featureReader,
            CreateGraphProviderWithService("svc-7", layerId: 7),
            evaluator,
            editionPolicy,
            new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance),
            NullLogger<AlertPipeline>.Instance);

        _ = await sut.ProcessChangesAsync(0, 10, cancellationToken: CancellationToken.None);

        reader.Loads.Should().Be(1);
        observed.Should().Equal(false);
        await featureReader.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SweepDwellAsync_ReevaluatesCooldownSuppressedThreshold_WithoutANewEdit()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var rule = CreateRule(AlertTriggerType.Threshold) with
        {
            ConditionsJson = """{"field":"value","operator":">","value":50}""",
            CooldownSeconds = 300,
            Channels = ImmutableArray<AlertChannelType>.Empty
        };
        var due = new DateTimeOffset(2026, 3, 8, 10, 5, 0, TimeSpan.Zero);
        var state = new AlertStateSnapshot
        {
            RuleId = rule.RuleId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            Inside = false,
            LastAlertAt = due.AddMinutes(-5),
            LastGeneration = 4,
            ThresholdStateJson = """{"breached":true,"breachedSince":"2026-03-08T10:00:00.0000000+00:00","pendingEdge":"resolve","pendingDueAt":"2026-03-08T10:05:00.0000000+00:00"}"""
        };
        IReadOnlyList<AlertOutboxEntry>? captured = null;

        stateStore.GetDwellCandidatesAsync(due, 10, Arg.Any<CancellationToken>()).Returns([state]);
        ruleRepository.GetRulesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertRuleDefinition> { [rule.RuleId] = rule });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition>());
        featureReader.GetAsync(rule.LayerId, 100, Arg.Any<CancellationToken>())
            .Returns(FeatureWithValue(100, 10));
        editionPolicy.IsRuleAllowed(rule).Returns(true);
        outbox.CommitEvaluationAsync(
                Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
                Arg.Do<IReadOnlyList<AlertOutboxEntry>>(entries => captured = entries),
                Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(true));

        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            featureReader,
            CreateGraphProviderWithService("svc-7", layerId: 7),
            new DefaultAlertEvaluator(),
            editionPolicy,
            new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance),
            NullLogger<AlertPipeline>.Instance);

        var evaluated = await sut.SweepDwellAsync(due, 10, CancellationToken.None);

        evaluated.Should().Be(1);
        captured.Should().NotBeNull();
        captured.Should().ContainSingle();
        captured![0].AlertEvent.PayloadJson.Should().Contain("\"transition\":\"threshold_resolved\"");
    }

    [UnitTest]
    public async Task ProcessChangesAsync_BoundsPayloadToTheSelectedChannel_BeforeEnqueue()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var outbox = Substitute.For<IAlertOutboxWriter>();
        var featureReader = Substitute.For<IFeatureReader>();
        var evaluator = Substitute.For<IAlertEvaluator>();
        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        var rule = CreateRule(AlertTriggerType.Threshold) with
        {
            Channels = ImmutableArray.Create(AlertChannelType.AwsSns)
        };
        var geometry = new byte[5 * 1024 * 1024];
        var payload = new JsonObject
        {
            ["geometryWkbHex"] = Convert.ToHexString(geometry),
            ["attributes"] = new JsonObject { ["value"] = 60 },
            ["layerId"] = rule.LayerId,
            ["objectId"] = 100,
            ["generation"] = 1
        };
        var alertEvent = new AlertEventEnvelope
        {
            DedupeKey = "large",
            RuleId = rule.RuleId,
            ServiceId = rule.ServiceId,
            LayerId = rule.LayerId,
            ObjectId = 100,
            TriggerType = rule.TriggerType,
            Generation = 1,
            Severity = rule.Severity,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = payload.ToJsonString()
        };
        IReadOnlyList<AlertOutboxEntry>? captured = null;

        changeReader.GetChangesAfterAsync(0, 10, Arg.Any<CancellationToken>()).Returns(
        [
            new AlertChange
            {
                Generation = 1,
                LayerId = rule.LayerId,
                ObjectId = 100,
                Operation = AlertChangeOperation.Update,
                ChangedAt = DateTimeOffset.UnixEpoch,
                CommittedFeature = FeatureWithValue(100, 60)
            }
        ]);
        ruleRepository.GetActiveRulesAsync(Arg.Any<IReadOnlyCollection<AlertRuleLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>
            {
                [new AlertRuleLookupKey("svc-7", rule.LayerId)] = [rule]
            });
        ruleRepository.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition>());
        stateStore.GetManyAsync(Arg.Any<IReadOnlyCollection<AlertStateLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertStateLookupKey, AlertStateSnapshot>());
        editionPolicy.IsRuleAllowed(rule).Returns(true);
        editionPolicy.IsChannelAllowed(AlertChannelType.AwsSns).Returns(true);
        editionPolicy.IsChannelConfigured(AlertChannelType.AwsSns).Returns(true);
        evaluator.EvaluateAsync(
                Arg.Any<AlertChange>(),
                Arg.Any<Feature?>(),
                rule,
                Arg.Any<AlertZoneDefinition?>(),
                Arg.Any<AlertStateSnapshot?>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new AlertEvaluationResult
            {
                UpdatedState = CreateState(rule.RuleId, rule.LayerId, 100, 1),
                Events = ImmutableArray.Create(alertEvent)
            });
        outbox.CommitEvaluationAsync(
                Arg.Any<IReadOnlyCollection<AlertStateSnapshot>>(),
                Arg.Do<IReadOnlyList<AlertOutboxEntry>>(entries => captured = entries),
                Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(true));

        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            featureReader,
            CreateGraphProviderWithService("svc-7", layerId: 7),
            evaluator,
            editionPolicy,
            new AlertDispatchWriter(outbox, TestTelemetry.CreateAlertPipelineMetrics(), NullLogger<AlertDispatchWriter>.Instance),
            NullLogger<AlertPipeline>.Instance);

        _ = await sut.ProcessChangesAsync(0, 10, cancellationToken: CancellationToken.None);

        captured.Should().NotBeNull();
        var stored = captured.Should().ContainSingle().Subject.AlertEvent.PayloadJson;
        System.Text.Encoding.UTF8.GetByteCount(stored).Should().BeLessThanOrEqualTo(AlertPayloadBounds.SnsLimitBytes);
        stored.Should().Contain("\"geometryOmitted\":true");
        stored.Should().NotContain("geometryWkbHex");
    }

    private sealed class ProjectingChangeReader(AlertChange change) : IAlertChangeReader, IAlertCommittedSnapshotSource
    {
        public int Loads { get; private set; }

        public Task<IReadOnlyList<AlertChange>> GetChangesAfterAsync(
            long lastGeneration,
            int maxCount,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlertChange>>([change]);

        public Task<Feature?> LoadProjectedSnapshotAsync(
            long generation,
            int layerId,
            CancellationToken cancellationToken = default)
        {
            Loads++;
            var raw = change.CommittedFeature!.Value;
            return Task.FromResult<Feature?>(raw with { Attributes = raw.Attributes.Remove("secret") });
        }
    }

    private static AlertChange CommittedChange(long generation, long value)
        => new()
        {
            Generation = generation,
            LayerId = 7,
            ObjectId = 100,
            Operation = AlertChangeOperation.Update,
            ChangedAt = DateTimeOffset.UnixEpoch.AddMinutes(generation),
            CommittedFeature = FeatureWithValue(100, value),
        };

    private static Feature FeatureWithValue(long objectId, long value)
        => Feature.Create(objectId, geometry: null, ImmutableDictionary<string, object?>.Empty.Add("value", value));

    private static AlertRuleDefinition CreateRule(AlertTriggerType triggerType = AlertTriggerType.Enter)
        => new()
        {
            RuleId = 99,
            ServiceId = "svc-7",
            LayerId = 7,
            ZoneId = 500,
            RuleName = "enter-zone",
            TriggerType = triggerType,
            Severity = AlertSeverity.Warning,
            EditionRequired = AlertEdition.Pro,
            Channels = ImmutableArray<AlertChannelType>.Empty,
            IsActive = true
        };

    private static AlertZoneDefinition CreateZone(long zoneId)
        => new()
        {
            ZoneId = zoneId,
            ServiceId = "svc-7",
            ZoneName = "Zone 500",
            IsActive = true
        };

    private static TestMetadataV2GraphProvider CreateGraphProviderWithService(string serviceName, int layerId)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource.alerts", $"layer-{layerId}")
            .AddService($"service.{serviceName}", serviceName)
            .AddPublication(
                $"pub.{serviceName}.{layerId}",
                $"service.{serviceName}",
                "resource.alerts",
                layerIndex: layerId)
            .Build();
        return new TestMetadataV2GraphProvider(graph);
    }

    private static Feature CreateFeature(long objectId)
        => Feature.Create(objectId, geometry: null, ImmutableDictionary<string, object?>.Empty);

    private static AlertStateSnapshot CreateState(long ruleId, int layerId, long objectId, long generation)
        => new()
        {
            RuleId = ruleId,
            LayerId = layerId,
            ObjectId = objectId,
            Inside = true,
            LastGeneration = generation
        };
}
