// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;

namespace Honua.Server.Features.Alerts;

internal sealed partial class AlertPipeline : IAlertPipeline
{
    private readonly IAlertChangeReader _changeReader;
    private readonly IAlertRuleRepository _ruleRepository;
    private readonly IAlertStateStore _stateStore;
    private readonly IAlertEventStore _eventStore;
    private readonly IAlertDispatchStore _dispatchStore;
    private readonly IFeatureReader _featureReader;
    private readonly ILayerCatalog _layerCatalog;
    private readonly IAlertEvaluator _evaluator;
    private readonly IAlertEditionPolicy _editionPolicy;
    private readonly ILogger<AlertPipeline> _logger;

    public AlertPipeline(
        IAlertChangeReader changeReader,
        IAlertRuleRepository ruleRepository,
        IAlertStateStore stateStore,
        IAlertEventStore eventStore,
        IAlertDispatchStore dispatchStore,
        IFeatureReader featureReader,
        ILayerCatalog layerCatalog,
        IAlertEvaluator evaluator,
        IAlertEditionPolicy editionPolicy,
        ILogger<AlertPipeline> logger)
    {
        _changeReader = changeReader ?? throw new ArgumentNullException(nameof(changeReader));
        _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _dispatchStore = dispatchStore ?? throw new ArgumentNullException(nameof(dispatchStore));
        _featureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _editionPolicy = editionPolicy ?? throw new ArgumentNullException(nameof(editionPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<long> ProcessChangesAsync(
        long lastProcessedGeneration,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var changes = await _changeReader
            .GetChangesAfterAsync(lastProcessedGeneration, batchSize, cancellationToken)
            .ConfigureAwait(false);

        if (changes.Count == 0)
        {
            return lastProcessedGeneration;
        }

        var serviceLookup = await BuildLayerServiceLookupAsync(cancellationToken).ConfigureAwait(false);
        var maxGeneration = lastProcessedGeneration;

        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.Generation > maxGeneration)
            {
                maxGeneration = change.Generation;
            }

            var serviceId = serviceLookup.TryGetValue(change.LayerId, out var mappedService)
                ? mappedService
                : null;

            var rules = await _ruleRepository
                .GetActiveRulesAsync(serviceId, change.LayerId, cancellationToken)
                .ConfigureAwait(false);
            if (rules.Count == 0)
            {
                continue;
            }

            var feature = change.Operation == AlertChangeOperation.Delete
                ? null
                : await _featureReader.GetAsync(change.LayerId, change.ObjectId, cancellationToken).ConfigureAwait(false);

            var zones = await LoadZonesAsync(rules, cancellationToken).ConfigureAwait(false);
            var evaluatedAt = DateTimeOffset.UtcNow;

            foreach (var rule in rules)
            {
                if (!rule.IsActive || !_editionPolicy.IsRuleAllowed(rule))
                {
                    continue;
                }

                var currentState = await _stateStore
                    .GetAsync(rule.RuleId, change.LayerId, change.ObjectId, cancellationToken)
                    .ConfigureAwait(false);

                var zone = ResolveZone(rule.ZoneId, zones);

                var evaluation = await _evaluator
                    .EvaluateAsync(change, feature, rule, zone, currentState, evaluatedAt, cancellationToken)
                    .ConfigureAwait(false);

                if (evaluation.UpdatedState is not null)
                {
                    await _stateStore.UpsertAsync(evaluation.UpdatedState, cancellationToken).ConfigureAwait(false);
                }

                if (evaluation.Events.IsDefaultOrEmpty)
                {
                    continue;
                }

                foreach (var alertEvent in evaluation.Events)
                {
                    var eventId = await _eventStore.TryAppendAsync(alertEvent, cancellationToken).ConfigureAwait(false);
                    if (!eventId.HasValue)
                    {
                        LogEventDeduplicated(_logger, alertEvent.DedupeKey, alertEvent.RuleId, alertEvent.ObjectId);
                        continue;
                    }

                    var channels = rule.Channels
                        .Where(_editionPolicy.IsChannelAllowed)
                        .Distinct()
                        .ToImmutableArray();

                    await _dispatchStore.EnqueueAsync(eventId.Value, channels, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return maxGeneration;
    }

    public async Task<int> SweepDwellAsync(
        DateTimeOffset evaluatedAt,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var states = await _stateStore
            .GetDwellCandidatesAsync(evaluatedAt, batchSize, cancellationToken)
            .ConfigureAwait(false);

        if (states.Count == 0)
        {
            return 0;
        }

        var evaluated = 0;
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rule = await _ruleRepository.GetRuleAsync(state.RuleId, cancellationToken).ConfigureAwait(false);
            if (rule is null || !rule.IsActive || rule.TriggerType != AlertTriggerType.Dwell)
            {
                continue;
            }

            if (!_editionPolicy.IsRuleAllowed(rule))
            {
                continue;
            }

            var zone = await LoadZoneForRuleAsync(rule, cancellationToken).ConfigureAwait(false);
            var feature = await _featureReader.GetAsync(state.LayerId, state.ObjectId, cancellationToken).ConfigureAwait(false);

            var syntheticChange = new AlertChange
            {
                Generation = state.LastGeneration,
                LayerId = state.LayerId,
                ObjectId = state.ObjectId,
                Operation = AlertChangeOperation.Update,
                ChangedAt = evaluatedAt
            };

            var evaluation = await _evaluator
                .EvaluateAsync(syntheticChange, feature, rule, zone, state, evaluatedAt, cancellationToken)
                .ConfigureAwait(false);

            if (evaluation.UpdatedState is not null)
            {
                await _stateStore.UpsertAsync(evaluation.UpdatedState, cancellationToken).ConfigureAwait(false);
            }

            foreach (var alertEvent in evaluation.Events)
            {
                var eventId = await _eventStore.TryAppendAsync(alertEvent, cancellationToken).ConfigureAwait(false);
                if (!eventId.HasValue)
                {
                    continue;
                }

                var channels = rule.Channels
                    .Where(_editionPolicy.IsChannelAllowed)
                    .Distinct()
                    .ToImmutableArray();

                await _dispatchStore.EnqueueAsync(eventId.Value, channels, cancellationToken).ConfigureAwait(false);
            }

            evaluated++;
        }

        return evaluated;
    }

    private async Task<Dictionary<int, string>> BuildLayerServiceLookupAsync(CancellationToken cancellationToken)
    {
        var services = await _layerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);
        var lookup = new Dictionary<int, string>();

        foreach (var service in services)
        {
            foreach (var layer in service.Layers)
            {
                _ = lookup.TryAdd(layer.Id, service.Name);
            }
        }

        return lookup;
    }

    private async Task<IReadOnlyDictionary<long, AlertZoneDefinition>> LoadZonesAsync(
        IReadOnlyList<AlertRuleDefinition> rules,
        CancellationToken cancellationToken)
    {
        var zoneIds = rules
            .Where(static rule => rule.ZoneId.HasValue)
            .Select(static rule => rule.ZoneId!.Value)
            .Distinct()
            .ToArray();

        return await _ruleRepository.GetZonesAsync(zoneIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AlertZoneDefinition?> LoadZoneForRuleAsync(AlertRuleDefinition rule, CancellationToken cancellationToken)
    {
        if (!rule.ZoneId.HasValue)
        {
            return null;
        }

        var zones = await _ruleRepository
            .GetZonesAsync(new[] { rule.ZoneId.Value }, cancellationToken)
            .ConfigureAwait(false);

        return ResolveZone(rule.ZoneId, zones);
    }

    private static AlertZoneDefinition? ResolveZone(
        long? zoneId,
        IReadOnlyDictionary<long, AlertZoneDefinition> zones)
    {
        if (!zoneId.HasValue)
        {
            return null;
        }

        return zones.TryGetValue(zoneId.Value, out var zone) ? zone : null;
    }

    [LoggerMessage(EventId = 9401, Level = LogLevel.Debug, Message = "Alert event deduplicated for key {DedupeKey} (rule {RuleId}, object {ObjectId}).")]
    private static partial void LogEventDeduplicated(ILogger logger, string dedupeKey, long ruleId, long objectId);
}
