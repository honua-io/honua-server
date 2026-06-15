// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Plugins;

/// <summary>
/// Per-plugin failure-isolation circuit breaker (issue #1562). Tracks consecutive faults of a
/// plugin's best-effort extension point (after-edit hooks, computed-field providers) and, once a
/// threshold of consecutive failures is reached, trips open so the host stops invoking the faulting
/// plugin until a cooldown elapses. This bounds the cost of a misbehaving plugin: rather than
/// throwing on every request, a repeatedly-faulting plugin is auto-disabled and periodically
/// retried (half-open) so a transient fault self-heals while a persistent one stays parked.
/// </summary>
/// <remarks>
/// The breaker is keyed by plugin id and is safe for concurrent use. A successful execution resets
/// the consecutive-failure count and closes the breaker. State transitions are reported to the
/// supplied callbacks so the host can audit/log auto-disable and recovery without coupling the
/// breaker to a specific sink.
/// </remarks>
internal sealed class PluginCircuitBreaker
{
    /// <summary>Default consecutive failures before the breaker trips open.</summary>
    public const int DefaultFailureThreshold = 3;

    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="PluginCircuitBreaker"/> class.</summary>
    /// <param name="failureThreshold">Consecutive failures that trip the breaker open. Must be positive.</param>
    /// <param name="cooldown">How long the breaker stays open before a half-open retry is allowed.</param>
    /// <param name="timeProvider">Time source (injectable for tests); defaults to <see cref="TimeProvider.System"/>.</param>
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

    /// <summary>
    /// Returns whether the plugin may currently be invoked. A closed breaker always allows; an open
    /// breaker allows a single half-open trial once the cooldown has elapsed, otherwise blocks.
    /// </summary>
    /// <param name="pluginId">The plugin id.</param>
    /// <returns><see langword="true"/> when invocation is permitted.</returns>
    public bool IsAllowed(string pluginId)
    {
        if (!_states.TryGetValue(pluginId, out var state) || !state.IsOpen)
        {
            return true;
        }

        return _timeProvider.GetUtcNow() >= state.OpenedUntil;
    }

    /// <summary>Records a successful execution, resetting failures and closing the breaker.</summary>
    /// <param name="pluginId">The plugin id.</param>
    /// <returns><see langword="true"/> when this success closed a previously-open breaker.</returns>
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

    /// <summary>
    /// Records a failed execution. Returns <see langword="true"/> when this failure tripped the
    /// breaker open (i.e. the plugin was just auto-disabled), so the caller can audit/log the
    /// transition exactly once.
    /// </summary>
    /// <param name="pluginId">The plugin id.</param>
    /// <returns><see langword="true"/> when the breaker transitioned to open on this failure.</returns>
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

    /// <summary>Gets whether the breaker for the given plugin is currently tripped open.</summary>
    /// <param name="pluginId">The plugin id.</param>
    /// <returns><see langword="true"/> when the breaker is open.</returns>
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
