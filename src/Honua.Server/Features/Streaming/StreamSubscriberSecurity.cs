// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Immutable subscriber read policies, resolved with the request identity and applied
/// to each stored event image before live or replay delivery.
/// </summary>
internal sealed class StreamSubscriberSecurity(
    HashSet<string> exactServiceIds,
    IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> exactRoutes,
    IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> namedRoutes)
{
    public bool HasRowPredicates { get; } = exactRoutes.Values.Any(policy => policy.Predicates.Count > 0);

    public bool Allows(FeatureStreamEnvelope envelope)
    {
        var policy = Resolve(envelope);
        if (policy is null)
        {
            return false;
        }

        foreach (var predicate in policy.Predicates)
        {
            if (envelope.Attributes is null || !InMemoryFilterEvaluator.Evaluate(predicate, envelope.Attributes))
            {
                return false;
            }
        }

        return true;
    }

    public FeatureStreamEnvelope Project(FeatureStreamEnvelope envelope)
    {
        var policy = Resolve(envelope)
            ?? throw new InvalidOperationException("The event has no subscriber read policy.");
        if (policy.MaskedFields.Count == 0)
        {
            return envelope;
        }

        return envelope with
        {
            Attributes = envelope.Attributes?.Where(pair => !policy.MaskedFields.Contains(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            ChangedAttributes = envelope.ChangedAttributes?.Where(pair => !policy.MaskedFields.Contains(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
        };
    }

    private StreamLayerReadPolicy? Resolve(FeatureStreamEnvelope envelope)
    {
        var isExact = exactServiceIds.Contains(envelope.ServiceId);
        var routes = isExact ? exactRoutes : namedRoutes;
        var service = isExact ? envelope.ServiceId : envelope.ServiceId.ToUpperInvariant();
        return routes.GetValueOrDefault((service, envelope.LayerId));
    }
}

/// <summary>All row restrictions and the union of field masks for one readable publication.</summary>
internal sealed record StreamLayerReadPolicy(
    IReadOnlyList<FilterExpression> Predicates,
    HashSet<string> MaskedFields);
