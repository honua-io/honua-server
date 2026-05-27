// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Alerts;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertPipelineTests
{
    [UnitTest]
    public async Task ProcessChangesAsync_BatchesRuleZoneAndStateLookups()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
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

        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            eventStore,
            dispatchStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            NullLogger<AlertPipeline>.Instance);

        var maxGeneration = await sut.ProcessChangesAsync(0, 10, CancellationToken.None);

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
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
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

        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            eventStore,
            dispatchStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
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
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
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

        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            eventStore,
            dispatchStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            NullLogger<AlertPipeline>.Instance);

        _ = await sut.ProcessChangesAsync(0, 10, CancellationToken.None);

        await stateStore.Received(1).UpsertManyAsync(
            Arg.Is<IReadOnlyCollection<AlertStateSnapshot>>(states =>
                states.Count == 2 &&
                states.Any(state => state.RuleId == ruleOne.RuleId) &&
                states.Any(state => state.RuleId == ruleTwo.RuleId)),
            Arg.Any<CancellationToken>());
        await stateStore.DidNotReceive().UpsertAsync(Arg.Any<AlertStateSnapshot>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SweepDwellAsync_BatchesStateUpsertsAcrossDueStates()
    {
        var changeReader = Substitute.For<IAlertChangeReader>();
        var ruleRepository = Substitute.For<IAlertRuleRepository>();
        var stateStore = Substitute.For<IAlertStateStore>();
        var eventStore = Substitute.For<IAlertEventStore>();
        var dispatchStore = Substitute.For<IAlertDispatchStore>();
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

        var sut = new AlertPipeline(
            changeReader,
            ruleRepository,
            stateStore,
            eventStore,
            dispatchStore,
            featureReader,
            graphProvider,
            evaluator,
            editionPolicy,
            NullLogger<AlertPipeline>.Instance);

        _ = await sut.SweepDwellAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);

        await stateStore.Received(1).UpsertManyAsync(
            Arg.Is<IReadOnlyCollection<AlertStateSnapshot>>(states =>
                states.Count == 2 &&
                states.Any(state => state.ObjectId == 100L) &&
                states.Any(state => state.ObjectId == 101L)),
            Arg.Any<CancellationToken>());
        await stateStore.DidNotReceive().UpsertAsync(Arg.Any<AlertStateSnapshot>(), Arg.Any<CancellationToken>());
    }

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
