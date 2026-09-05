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
    public bool Allows(FeatureStreamEnvelope envelope)
    {
        var policy = Resolve(envelope);
        return policy is not null && policy.Predicates.All(predicate =>
            envelope.Attributes is not null && InMemoryFilterEvaluator.Evaluate(predicate, envelope.Attributes));
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
        var routes = exactServiceIds.Contains(envelope.ServiceId) ? exactRoutes : namedRoutes;
        var service = exactServiceIds.Contains(envelope.ServiceId) ? envelope.ServiceId : envelope.ServiceId.ToUpperInvariant();
        return routes.GetValueOrDefault((service, envelope.LayerId));
    }
}

/// <summary>All row restrictions and the union of field masks for one readable publication.</summary>
internal sealed record StreamLayerReadPolicy(
    IReadOnlyList<FilterExpression> Predicates,
    HashSet<string> MaskedFields);
