// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Plugins;

internal sealed class PluginCircuitBreaker
{
    public const int DefaultFailureThreshold = 3;

    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public PluginCircuitBreaker(
        int failureThreshold = DefaultFailureThreshold,
        TimeSpan? cooldown = null,
        TimeProvider? timeProvider = null)
    {
        if (failureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Failure threshold must be positive.");
        }

        _failureThreshold = failureThreshold;
        _cooldown = cooldown ?? DefaultCooldown;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsAllowed(string pluginId)
    {
        if (!_states.TryGetValue(pluginId, out var state) || !state.IsOpen)
        {
            return true;
        }

        return _timeProvider.GetUtcNow() >= state.OpenedUntil;
    }

    public bool RecordSuccess(string pluginId)
    {
        var recovered = false;
        _states.AddOrUpdate(
            pluginId,
            static _ => State.Closed,
            (_, existing) =>
            {
                recovered = existing.IsOpen;
                return State.Closed;
            });
        return recovered;
    }

    public bool RecordFailure(string pluginId)
    {
        var tripped = false;
        _states.AddOrUpdate(
            pluginId,
            _ =>
            {
                var first = State.Closed.WithFailure(_failureThreshold, _timeProvider.GetUtcNow(), _cooldown, out tripped);
                return first;
            },
            (_, existing) => existing.WithFailure(_failureThreshold, _timeProvider.GetUtcNow(), _cooldown, out tripped));
        return tripped;
    }

    public bool IsOpen(string pluginId)
        => _states.TryGetValue(pluginId, out var state) && state.IsOpen;

    private readonly record struct State(int ConsecutiveFailures, bool IsOpen, DateTimeOffset OpenedUntil)
    {
        public static State Closed { get; } = new(0, IsOpen: false, OpenedUntil: default);

        public State WithFailure(int threshold, DateTimeOffset now, TimeSpan cooldown, out bool tripped)
        {
            var failures = ConsecutiveFailures + 1;
            if (failures >= threshold)
            {
                // Trip (or re-arm an already-open breaker after a failed half-open trial). Report
                // "tripped" only on the closed -> open transition so the host audits it once.
                tripped = !IsOpen;
                return new State(failures, IsOpen: true, now + cooldown);
            }

            tripped = false;
            return new State(failures, IsOpen: false, OpenedUntil: default);
        }
    }
}
