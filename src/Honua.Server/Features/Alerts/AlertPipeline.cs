// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Alerts;

internal sealed partial class AlertPipeline : IAlertPipeline
{
    private readonly IAlertChangeReader _changeReader;
    private readonly IAlertRuleRepository _ruleRepository;
    private readonly IAlertStateStore _stateStore;
    private readonly IAlertEventStore _eventStore;
    private readonly IAlertDispatchStore _dispatchStore;
    private readonly IFeatureReader _featureReader;
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IAlertEvaluator _evaluator;
    private readonly IAlertEditionPolicy _editionPolicy;
    private readonly ILogger<AlertPipeline> _logger;
    private readonly record struct PendingAlertDispatch(
        AlertEventEnvelope AlertEvent,
        ImmutableArray<AlertChannelType> Channels);

    public AlertPipeline(
        IAlertChangeReader changeReader,
        IAlertRuleRepository ruleRepository,
        IAlertStateStore stateStore,
        IAlertEventStore eventStore,
        IAlertDispatchStore dispatchStore,
        IFeatureReader featureReader,
        IMetadataV2GraphProvider graphProvider,
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
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
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
        var rulesByLookup = await LoadRulesForChangesAsync(changes, serviceLookup, cancellationToken).ConfigureAwait(false);
        var zones = await LoadZonesAsync(
                rulesByLookup.Values.SelectMany(static rules => rules).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var stateByKey = await LoadStatesForChangesAsync(changes, serviceLookup, rulesByLookup, cancellationToken).ConfigureAwait(false);
        var featureCache = new Dictionary<(int LayerId, long ObjectId), Feature?>();
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

            var lookupKey = new AlertRuleLookupKey(serviceId, change.LayerId);
            rulesByLookup.TryGetValue(lookupKey, out var rules);
            rules ??= Array.Empty<AlertRuleDefinition>();
            if (rules.Count == 0)
            {
                continue;
            }

            var feature = change.Operation == AlertChangeOperation.Delete
                ? null
                : await GetFeatureAsync(change.LayerId, change.ObjectId, featureCache, cancellationToken).ConfigureAwait(false);
            var evaluatedAt = DateTimeOffset.UtcNow;
            var pendingStateUpdates = new List<AlertStateSnapshot>(rules.Count);
            var pendingDispatches = new List<PendingAlertDispatch>();

            foreach (var rule in rules)
            {
                if (!rule.IsActive || !_editionPolicy.IsRuleAllowed(rule))
                {
                    continue;
                }

                stateByKey.TryGetValue(new AlertStateLookupKey(rule.RuleId, change.LayerId, change.ObjectId), out var currentState);

                var zone = ResolveZone(rule.ZoneId, zones);

                var evaluation = await _evaluator
                    .EvaluateAsync(change, feature, rule, zone, currentState, evaluatedAt, cancellationToken)
                    .ConfigureAwait(false);

                if (evaluation.UpdatedState is not null)
                {
                    pendingStateUpdates.Add(evaluation.UpdatedState);
                    stateByKey[new AlertStateLookupKey(
                        evaluation.UpdatedState.RuleId,
                        evaluation.UpdatedState.LayerId,
                        evaluation.UpdatedState.ObjectId)] = evaluation.UpdatedState;
                }

                if (evaluation.Events.IsDefaultOrEmpty)
                {
                    continue;
                }

                foreach (var alertEvent in evaluation.Events)
                {
                    pendingDispatches.Add(new PendingAlertDispatch(alertEvent, GetDeliverableChannels(rule)));
                }
            }

            if (pendingStateUpdates.Count > 0)
            {
                await _stateStore.UpsertManyAsync(pendingStateUpdates, cancellationToken).ConfigureAwait(false);
            }

            await PersistDispatchesAsync(pendingDispatches, cancellationToken).ConfigureAwait(false);
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

        var ruleById = (await _ruleRepository
                .GetRulesAsync(states.Select(static state => state.RuleId).Distinct().ToArray(), cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary();
        var zones = await LoadZonesAsync(ruleById.Values.ToArray(), cancellationToken).ConfigureAwait(false);
        var featureCache = new Dictionary<(int LayerId, long ObjectId), Feature?>();
        var pendingStateUpdates = new List<AlertStateSnapshot>(states.Count);
        var pendingDispatches = new List<PendingAlertDispatch>();
        var evaluated = 0;
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ruleById.TryGetValue(state.RuleId, out var rule))
            {
                continue;
            }

            if (rule is null || !rule.IsActive || rule.TriggerType != AlertTriggerType.Dwell)
            {
                continue;
            }

            if (!_editionPolicy.IsRuleAllowed(rule))
            {
                continue;
            }

            var zone = ResolveZone(rule.ZoneId, zones);
            var feature = await GetFeatureAsync(state.LayerId, state.ObjectId, featureCache, cancellationToken).ConfigureAwait(false);

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
                pendingStateUpdates.Add(evaluation.UpdatedState);
            }

            foreach (var alertEvent in evaluation.Events)
            {
                pendingDispatches.Add(new PendingAlertDispatch(alertEvent, GetDeliverableChannels(rule)));
            }

            evaluated++;
        }

        if (pendingStateUpdates.Count > 0)
        {
            await _stateStore.UpsertManyAsync(pendingStateUpdates, cancellationToken).ConfigureAwait(false);
        }

        await PersistDispatchesAsync(pendingDispatches, cancellationToken).ConfigureAwait(false);

        return evaluated;
    }

    private async Task PersistDispatchesAsync(
        IReadOnlyList<PendingAlertDispatch> pendingDispatches,
        CancellationToken cancellationToken)
    {
        foreach (var pendingDispatch in pendingDispatches)
        {
            var eventId = await _eventStore.TryAppendAsync(pendingDispatch.AlertEvent, cancellationToken).ConfigureAwait(false);
            if (!eventId.HasValue)
            {
                LogEventDeduplicated(
                    _logger,
                    pendingDispatch.AlertEvent.DedupeKey,
                    pendingDispatch.AlertEvent.RuleId,
                    pendingDispatch.AlertEvent.ObjectId);
                continue;
            }

            await _dispatchStore.EnqueueAsync(eventId.Value, pendingDispatch.Channels, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>> LoadRulesForChangesAsync(
        IReadOnlyList<AlertChange> changes,
        Dictionary<int, string> serviceLookup,
        CancellationToken cancellationToken)
    {
        var lookupKeys = changes
            .Select(change => new AlertRuleLookupKey(
                serviceLookup.TryGetValue(change.LayerId, out var serviceId) ? serviceId : null,
                change.LayerId))
            .Distinct()
            .ToArray();

        var rules = await _ruleRepository
            .GetActiveRulesAsync(lookupKeys, cancellationToken)
            .ConfigureAwait(false);

        return lookupKeys.ToDictionary(
            static key => key,
            key => rules.TryGetValue(key, out var value) ? value : Array.Empty<AlertRuleDefinition>());
    }

    private async Task<Dictionary<AlertStateLookupKey, AlertStateSnapshot>> LoadStatesForChangesAsync(
        IReadOnlyList<AlertChange> changes,
        Dictionary<int, string> serviceLookup,
        Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>> rulesByLookup,
        CancellationToken cancellationToken)
    {
        var lookupKeys = new HashSet<AlertStateLookupKey>();

        foreach (var change in changes)
        {
            var ruleLookupKey = new AlertRuleLookupKey(
                serviceLookup.TryGetValue(change.LayerId, out var serviceId) ? serviceId : null,
                change.LayerId);

            if (!rulesByLookup.TryGetValue(ruleLookupKey, out var rules))
            {
                continue;
            }

            foreach (var rule in rules)
            {
                if (!rule.IsActive || !_editionPolicy.IsRuleAllowed(rule))
                {
                    continue;
                }

                lookupKeys.Add(new AlertStateLookupKey(rule.RuleId, change.LayerId, change.ObjectId));
            }
        }

        var states = await _stateStore
            .GetManyAsync(lookupKeys.ToArray(), cancellationToken)
            .ConfigureAwait(false);

        return states.ToDictionary();
    }

    private async Task<Dictionary<int, string>> BuildLayerServiceLookupAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var lookup = new Dictionary<int, string>();

        foreach (var service in snapshot.Graph.Services)
        {
            foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id))
            {
                if (publication.LayerIndex.HasValue)
                {
                    _ = lookup.TryAdd(publication.LayerIndex.Value, service.Metadata.Name);
                }
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

    private async Task<Feature?> GetFeatureAsync(
        int layerId,
        long objectId,
        Dictionary<(int LayerId, long ObjectId), Feature?> featureCache,
        CancellationToken cancellationToken)
    {
        var key = (layerId, objectId);
        if (featureCache.TryGetValue(key, out var cachedFeature))
        {
            return cachedFeature;
        }

        var feature = await _featureReader.GetAsync(layerId, objectId, cancellationToken).ConfigureAwait(false);
        featureCache[key] = feature;
        return feature;
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

    private ImmutableArray<AlertChannelType> GetDeliverableChannels(AlertRuleDefinition rule)
    {
        var builder = ImmutableArray.CreateBuilder<AlertChannelType>(rule.Channels.Length);

        foreach (var channel in rule.Channels.Distinct())
        {
            if (!_editionPolicy.IsChannelAllowed(channel))
            {
                LogChannelSkipped(_logger, rule.RuleId, channel, "edition");
                continue;
            }

            if (!_editionPolicy.IsChannelConfigured(channel))
            {
                LogChannelSkipped(_logger, rule.RuleId, channel, "configuration");
                continue;
            }

            builder.Add(channel);
        }

        return builder.ToImmutable();
    }

    [LoggerMessage(EventId = 9401, Level = LogLevel.Debug, Message = "Alert event deduplicated for key {DedupeKey} (rule {RuleId}, object {ObjectId}).")]
    private static partial void LogEventDeduplicated(ILogger logger, string dedupeKey, long ruleId, long objectId);

    [LoggerMessage(EventId = 9440, Level = LogLevel.Warning, Message = "Skipping alert channel {ChannelType} for rule {RuleId} because it is unavailable under the current {Reason} policy.")]
    private static partial void LogChannelSkipped(ILogger logger, long ruleId, AlertChannelType channelType, string reason);
}
