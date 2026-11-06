// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

public static class FeatureMutationOutboxScope
{
    private static readonly AsyncLocal<FeatureMutationOutboxScopeData?> _current = new();

    public static FeatureMutationOutboxScopeData? Current => _current.Value;

    // Begin must run synchronously on the caller's flow, because an async method's writes do not survive the caller's restored execution context.
    public static IDisposable Begin(FeatureMutationOutboxScopeData scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = _current.Value;
        _current.Value = scope;
        return new ScopeReleaser(previous);
    }

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

public sealed record FeatureMutationOutboxScopeData
{
    public required FeatureMutationOutboxEntryFactory EntryFactory { get; init; }

    public Action<string>? BeginRowAttempt { get; init; }
}

public delegate FeatureChangeOutboxEntry? FeatureMutationOutboxEntryFactory(
    long objectId,
    string operation,
    Feature? snapshot);
