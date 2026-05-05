// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Carries protocol-layer context (service id, protocol, request id, entry factory)
/// across the call chain from a protocol handler down into the data-access layer
/// so a feature mutation can write its outbox row inside the same transaction as
/// the row mutation. Stored on an <see cref="AsyncLocal{T}"/> so it flows through
/// awaits without forcing every <c>IFeatureWriter</c> consumer to plumb a new
/// argument.
/// </summary>
public static class FeatureMutationOutboxScope
{
    private static readonly AsyncLocal<FeatureMutationOutboxScopeData?> _current = new();

    /// <summary>
    /// Active outbox scope for the current async flow, or <c>null</c> when no
    /// protocol handler has begun one (e.g. internal callers, tests, or non-capable providers).
    /// </summary>
    public static FeatureMutationOutboxScopeData? Current => _current.Value;

    /// <summary>
    /// Begin a new scope. Returns an <see cref="IDisposable"/> that restores the
    /// previous scope on dispose. Always call from a <c>using</c> block so a
    /// throwing protocol handler does not leak scope into unrelated work on the same thread.
    /// </summary>
    /// <remarks>
    /// Must be invoked synchronously in the caller's async flow. <see cref="AsyncLocal{T}"/>
    /// mutations made inside an <c>async</c> method are not observed by the caller after
    /// the awaiter completes (the ExecutionContext snapshot the caller restores does not
    /// include the callee's writes), so async resolvers must return their <see cref="FeatureMutationOutboxScopeData"/>
    /// and the caller wraps it with <c>using var scope = FeatureMutationOutboxScope.BeginIfNotNull(...)</c>.
    /// </remarks>
    public static IDisposable Begin(FeatureMutationOutboxScopeData scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = _current.Value;
        _current.Value = scope;
        return new ScopeReleaser(previous);
    }

    /// <summary>
    /// Begin a new scope, or return a no-op disposable when <paramref name="scope"/> is null.
    /// Convenience for protocol handlers that resolve scope data through an <c>async</c>
    /// helper; the caller invokes this synchronously in its own ExecutionContext after the
    /// resolver's await completes.
    /// </summary>
    public static IDisposable BeginIfNotNull(FeatureMutationOutboxScopeData? scope)
        => scope is null ? NoopScope.Instance : Begin(scope);

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        private NoopScope() { }
        public void Dispose() { }
    }

    private sealed class ScopeReleaser(FeatureMutationOutboxScopeData? previous) : IDisposable
    {
        private readonly FeatureMutationOutboxScopeData? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current.Value = _previous;
        }
    }
}

/// <summary>
/// Per-mutation outbox scope. Carries the protocol-supplied factory used to construct
/// a <see cref="FeatureChangeOutboxEntry"/> from the post-mutation snapshot the data-access
/// layer holds.
/// </summary>
public sealed record FeatureMutationOutboxScopeData
{
    /// <summary>
    /// Factory invoked once per successful row mutation, inside the mutation transaction,
    /// to materialize the outbox row. Implementations capture protocol-layer state
    /// (service id, protocol, request id, layer SRID) and serialize the canonical
    /// <c>FeatureChangeEventRequest</c> payload.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> when the scope opts out for a particular row (for example
    /// a successful create whose returning snapshot did not satisfy the geodesy pair contract).
    /// </remarks>
    public required FeatureMutationOutboxEntryFactory EntryFactory { get; init; }
}

/// <summary>
/// Builds a <see cref="FeatureChangeOutboxEntry"/> from the post-mutation snapshot the
/// data-access layer captured inside the transaction.
/// </summary>
/// <param name="objectId">Mutated feature's object identifier.</param>
/// <param name="operation">Operation kind: <c>create</c>, <c>update</c>, or <c>delete</c>.</param>
/// <param name="snapshot">
/// Post-mutation feature snapshot for create/update; pre-deletion snapshot for delete.
/// Null when the data-access layer could not capture a snapshot (e.g. delete with no RETURNING).
/// </param>
public delegate FeatureChangeOutboxEntry? FeatureMutationOutboxEntryFactory(
    long objectId,
    string operation,
    Feature? snapshot);
