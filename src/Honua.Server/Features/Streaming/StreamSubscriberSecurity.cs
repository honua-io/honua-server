// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Streaming;

// The policy snapshot is replaced as a whole for matching and delivery.
internal sealed class StreamSubscriberSecurity
{
    private PolicyState _state;
    private Func<CancellationToken, Task<StreamSubscriberSecurity>>? _refresh;
    private int _eventDriven;
    private Task? _retired;
    private readonly Channel<byte> _refreshGate = Channel.CreateBounded<byte>(1);

    public StreamSubscriberSecurity(
        HashSet<string> exactServiceIds,
        IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> exactRoutes,
        IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> namedRoutes,
        IReadOnlySet<(string Service, int Layer)>? deniedExactRoutes = null)
    {
        _refreshGate.Writer.TryWrite(0);
        _state = new PolicyState(
            exactServiceIds,
            exactRoutes,
            namedRoutes,
            deniedExactRoutes ?? new HashSet<(string Service, int Layer)>());
    }

    internal Func<CancellationToken, Task>? ReplayRevalidateAsync { get; set; }

    internal void BindLifetime(Task retired) => Volatile.Write(ref _retired, retired);

    internal Task<bool> RefreshActivePolicyAsync(CancellationToken cancellationToken)
        => RefreshUntilRetiredAsync(Volatile.Read(ref _retired), cancellationToken);

    // Races the refresh against the generation's retirement: a retired generation never waits on,
    // or fails because of, a refresh it no longer needs.
    internal async Task<bool> RefreshUntilRetiredAsync(Task? retired, CancellationToken cancellationToken)
    {
        if (retired?.IsCompleted == true)
        {
            return true;
        }

        using var refreshStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var refresh = RefreshAsync(refreshStop.Token);
        try
        {
            if (retired is not null
                && await Task.WhenAny(refresh, retired).WaitAsync(cancellationToken).ConfigureAwait(false) == retired)
            {
                await refreshStop.CancelAsync().ConfigureAwait(false);
                _ = refresh.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return true;
            }

            var outcome = await refresh.ConfigureAwait(false);
            return retired?.IsCompleted == true || outcome != StreamSubscriberSecurityRefreshOutcome.Revoked;
        }
        catch (Exception error) when (error is not OutOfMemoryException && retired?.IsCompleted == true)
        {
            return true;
        }
    }

    internal async Task RefreshForReplayAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _retired)?.IsCompleted == true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        if (ReplayRevalidateAsync is { } revalidate)
        {
            await revalidate(cancellationToken).ConfigureAwait(false);
        }
        else if (!await RefreshActivePolicyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Feature stream access was revoked.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    // Readable layers whose row policy for this subscriber cannot be applied to stream events.
    // Read only at subscribe time: a refresh replaces the routes but not this set.
    public IReadOnlySet<int> UnstreamablePolicyLayers { get; init; } = new HashSet<int>();

    // A readable layer whose row policy cannot be evaluated on stream events (an area
    // restriction, for example) is denied rather than filtered. A subscription that names such
    // a layer, with no other readable route for it, would only ever be silent, so it is refused.
    public string? DescribeUnstreamableLayer(IReadOnlyList<int>? explicitLayerIds)
    {
        if (explicitLayerIds is null || UnstreamablePolicyLayers.Count == 0)
        {
            return null;
        }

        var routes = Volatile.Read(ref _state).ExactRoutes.Keys;
        foreach (var layerId in explicitLayerIds)
        {
            if (UnstreamablePolicyLayers.Contains(layerId) && !routes.Any(route => route.Layer == layerId))
            {
                return $"Layer {layerId} has a read restriction that feature streams cannot apply (for example, a row-level security area), so the stream would deliver no changes. Read the layer with a feature query instead.";
            }
        }

        return null;
    }

    public bool HasRowPredicates => Volatile.Read(ref _state).HasRowPredicates;

    public bool IsRefreshable => Volatile.Read(ref _refresh) is not null && Volatile.Read(ref _eventDriven) == 0;

    public void EnableEventDrivenRefresh() => Volatile.Write(ref _eventDriven, 1);

    public bool Allows(FeatureStreamEnvelope envelope)
    {
        var policy = Volatile.Read(ref _state).Resolve(envelope);
        return policy is not null && AllowsRow(policy, envelope.Attributes);
    }

    private static bool AllowsRow(StreamLayerReadPolicy policy, IReadOnlyDictionary<string, JsonElement>? attributes)
    {
        if (!policy.SupportsStreaming)
        {
            return false;
        }

        if (policy.RequiredFields.Any(field => attributes?.ContainsKey(field) != true))
        {
            return false;
        }

        foreach (var predicate in policy.Predicates)
        {
            if (attributes is null || !InMemoryFilterEvaluator.Evaluate(predicate, attributes))
            {
                return false;
            }
        }

        return true;
    }

    // A client predicate may only read fields the subscriber can read on the event's route. A
    // route with no current policy is not readable at all, so it counts as masked.
    public bool MasksAny(FeatureStreamEnvelope envelope, IReadOnlyList<string> fields)
    {
        var policy = Volatile.Read(ref _state).Resolve(envelope);
        return policy is null || FirstMaskedField(policy, fields) is not null;
    }

    public string? FindMaskedField(IReadOnlyList<string> fields)
        => Volatile.Read(ref _state).ExactRoutes.Values
            .Select(policy => FirstMaskedField(policy, fields))
            .FirstOrDefault(static field => field is not null);

    private static string? FirstMaskedField(StreamLayerReadPolicy policy, IReadOnlyList<string> fields)
    {
        if (policy.MaskedFields.Count == 0)
        {
            return null;
        }

        for (var index = 0; index < fields.Count; index++)
        {
            if (policy.MaskedFields.Contains(fields[index]))
            {
                return fields[index];
            }
        }

        return null;
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

        return Project(envelope, policy);
    }

    private static FeatureStreamEnvelope Project(FeatureStreamEnvelope envelope, StreamLayerReadPolicy policy)
    {
        if (policy.MaskedFields.Count == 0)
        {
            return envelope;
        }

        return envelope with
        {
            AuthorizationAttributes = envelope.AuthorizationAttributes ?? envelope.Attributes,
            Attributes = envelope.Attributes?.Where(pair => !policy.MaskedFields.Contains(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            ChangedAttributes = envelope.ChangedAttributes?.Where(pair => !policy.MaskedFields.Contains(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
        };
    }

    public FeatureStreamEnvelope? PrepareDelivery(FeatureStreamEnvelope envelope, IReadOnlyList<string> filterFields)
    {
        var policy = Volatile.Read(ref _state).Resolve(envelope);
        if (policy is null ||
            (!string.Equals(envelope.Operation, "delete", StringComparison.OrdinalIgnoreCase) &&
             FirstMaskedField(policy, filterFields) is not null))
        {
            return null;
        }

        var attributes = envelope.AuthorizationAttributes ?? envelope.Attributes;
        try
        {
            if (!AllowsRow(policy, attributes))
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is UnevaluableLikePatternException or RegexMatchTimeoutException)
        {
            return null;
        }

        return Project(envelope, policy);
    }

    // Refreshes replace the cached policy; frame delivery reads only that cache.
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

        await _refreshGate.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

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
            _refreshGate.Writer.TryWrite(0);
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

        private readonly Lazy<IReadOnlyDictionary<int, StreamLayerReadPolicy>> _byLayer = new(() => CombineByLayer(exactRoutes));

        public StreamLayerReadPolicy? Resolve(FeatureStreamEnvelope envelope)
        {
            // A change made directly in the database names no service, so it is read under every
            // readable publication of its layer at once: all their predicates, all their masks.
            if (envelope.ServiceId.Length == 0)
            {
                return _byLayer.Value.GetValueOrDefault(envelope.LayerId);
            }

            var isExact = exactServiceIds.Contains(envelope.ServiceId);
            var routes = isExact ? exactRoutes : namedRoutes;
            var service = isExact ? envelope.ServiceId : envelope.ServiceId.ToUpperInvariant();
            return routes.GetValueOrDefault((service, envelope.LayerId));
        }

        private static Dictionary<int, StreamLayerReadPolicy> CombineByLayer(
            IReadOnlyDictionary<(string Service, int Layer), StreamLayerReadPolicy> routes)
            => routes
                .GroupBy(route => route.Key.Layer)
                .ToDictionary(
                    layer => layer.Key,
                    layer => new StreamLayerReadPolicy(
                        layer.SelectMany(route => route.Value.Predicates).ToArray(),
                        layer.SelectMany(route => route.Value.MaskedFields).ToHashSet(StringComparer.OrdinalIgnoreCase)));
    }
}

internal enum StreamSubscriberSecurityRefreshOutcome
{
    NotRefreshable,

    Refreshed,

    // A route the subscriber could read that is denied ends the subscription.
    Revoked
}

internal sealed record StreamLayerReadPolicy(
    IReadOnlyList<FilterExpression> Predicates,
    HashSet<string> MaskedFields)
{
    public IReadOnlyList<string> RequiredFields { get; } = Predicates
        .SelectMany(FeatureQuerySecurity.CollectFieldReferences).Distinct(StringComparer.Ordinal).ToArray();

    public bool SupportsStreaming { get; } = Predicates.All(static predicate =>
        InMemoryFilterEvaluator.TryValidateStreamingExpression(predicate, out _));
}
