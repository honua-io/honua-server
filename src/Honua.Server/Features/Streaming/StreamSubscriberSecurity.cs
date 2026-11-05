// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Streaming;

// The policy snapshot is replaced as a whole, so a change applies to frames matched after the refresh.
internal sealed class StreamSubscriberSecurity
{
    private PolicyState _state;
    private Func<CancellationToken, Task<StreamSubscriberSecurity>>? _refresh;
    private int _refreshing;

    public StreamSubscriberSecurity(
        HashSet<string> exactServiceIds,
        IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> exactRoutes,
        IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> namedRoutes,
        IReadOnlySet<(string Service, int Layer)>? deniedExactRoutes = null)
    {
        _state = new PolicyState(
            exactServiceIds,
            exactRoutes,
            namedRoutes,
            deniedExactRoutes ?? new HashSet<(string Service, int Layer)>());
    }

    public bool HasRowPredicates => Volatile.Read(ref _state).HasRowPredicates;

    public bool IsRefreshable => Volatile.Read(ref _refresh) is not null;

    public bool Allows(FeatureStreamEnvelope envelope)
    {
        var policy = Volatile.Read(ref _state).Resolve(envelope);
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
        var policy = Volatile.Read(ref _state).Resolve(envelope);
        if (policy is null)
        {
            // A refresh can retract the publication between Allows and Project. Never let a
            // concurrent swap throw into live fan-out, and never emit attributes the current
            // policy no longer authorizes.
            return envelope with { Attributes = null, ChangedAttributes = null };
        }

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

    // The refresh source runs on the tick, never on each event.
    public void EnableRefresh(Func<CancellationToken, Task<StreamSubscriberSecurity>> refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        Volatile.Write(ref _refresh, refresh);
    }

    public async Task<StreamSubscriberSecurityRefreshOutcome> RefreshAsync(CancellationToken cancellationToken)
    {
        var refresh = Volatile.Read(ref _refresh);
        if (refresh is null)
        {
            return StreamSubscriberSecurityRefreshOutcome.NotRefreshable;
        }

        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return StreamSubscriberSecurityRefreshOutcome.InProgress;
        }

        try
        {
            var refreshed = await refresh(cancellationToken).ConfigureAwait(false);
            var next = Volatile.Read(ref refreshed._state);
            var previous = Interlocked.Exchange(ref _state, next);
            foreach (var route in previous.ExactRoutes.Keys)
            {
                if (next.DeniedExactRoutes.Contains(route))
                {
                    return StreamSubscriberSecurityRefreshOutcome.Revoked;
                }
            }

            return StreamSubscriberSecurityRefreshOutcome.Refreshed;
        }
        catch
        {
            Volatile.Write(ref _state, PolicyState.DenyAll);
            throw;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private sealed class PolicyState(
        HashSet<string> exactServiceIds,
        IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> exactRoutes,
        IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> namedRoutes,
        IReadOnlySet<(string Service, int Layer)> deniedExactRoutes)
    {
        public static PolicyState DenyAll { get; } = new(
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<(string Service, int Layer), StreamLayerReadPolicy>(),
            new Dictionary<(string Service, int Layer), StreamLayerReadPolicy>(),
            new HashSet<(string Service, int Layer)>());

        public bool HasRowPredicates { get; } = exactRoutes.Values.Any(policy => policy.Predicates.Count > 0);

        public IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> ExactRoutes => exactRoutes;

        public IReadOnlySet<(string Service, int Layer)> DeniedExactRoutes => deniedExactRoutes;

        public StreamLayerReadPolicy? Resolve(FeatureStreamEnvelope envelope)
        {
            var isExact = exactServiceIds.Contains(envelope.ServiceId);
            var routes = isExact ? exactRoutes : namedRoutes;
            var service = isExact ? envelope.ServiceId : envelope.ServiceId.ToUpperInvariant();
            return routes.GetValueOrDefault((service, envelope.LayerId));
        }
    }
}

internal enum StreamSubscriberSecurityRefreshOutcome
{
    NotRefreshable,

    InProgress,

    Refreshed,

    // A route the subscriber could read that is denied ends the subscription.
    Revoked
}

internal sealed record StreamLayerReadPolicy(
    IReadOnlyList<FilterExpression> Predicates,
    HashSet<string> MaskedFields);
