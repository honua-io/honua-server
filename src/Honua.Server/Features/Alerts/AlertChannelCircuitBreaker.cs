// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

/// <summary>
/// Process-local, per-channel delivery circuit breaker that bounds dead-letter volume when a
/// notification channel is persistently failing (for example a dead ops webhook whose host no
/// longer resolves). Without it, a channel that fails every attempt dead-letters once per
/// recurring event — deploy/job ops notifications recur, so the dead-letter path is spammed.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher records a success (<see cref="RecordSuccess"/>) or a dead-letter
/// (<see cref="RecordDeadLetter"/>) per channel. After <see cref="AlertDispatchOptions.CircuitBreakerThreshold"/>
/// consecutive dead-letters the breaker opens for <see cref="AlertDispatchOptions.CircuitBreakerCooldown"/>.
/// While open, <see cref="ShouldAttemptDelivery"/> returns <c>false</c> so the dispatcher defers
/// (reschedules) claimed rows instead of driving them through the full retry→dead-letter cycle,
/// and <see cref="Honua.Alerts.Ops.OpsNotificationService"/> skips the channel when composing new
/// notifications so no
/// fresh dead-letter rows accrue. Once the cooldown elapses a single half-open probe is admitted; a
/// success closes the breaker, another failure re-opens it.
/// </para>
/// <para>
/// State is per replica (in-memory), mirroring the per-replica notification rate cap. This bounds
/// dead-letter volume for a dead channel to roughly one dead-letter per cooldown window per replica.
/// </para>
/// </remarks>
internal sealed class AlertChannelCircuitBreaker
{
    private readonly ConcurrentDictionary<AlertChannelType, ChannelState> _states = new();
    private readonly AlertDispatchOptions _dispatch;

    public AlertChannelCircuitBreaker(IOptions<AlertOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dispatch = options.Value.Dispatch;
    }

    private AlertDispatchOptions Dispatch => _dispatch;

    /// <summary>
    /// Whether a delivery attempt should proceed on the channel now. Returns <c>true</c> when the
    /// breaker is closed, disabled, or when the cooldown has elapsed and this call takes the single
    /// half-open probe. Returns <c>false</c> while the breaker is open and cooling.
    /// </summary>
    public bool ShouldAttemptDelivery(AlertChannelType channel, DateTimeOffset now)
    {
        if (Dispatch.CircuitBreakerThreshold <= 0)
        {
            return true;
        }

        if (!_states.TryGetValue(channel, out var state))
        {
            return true;
        }

        lock (state.Sync)
        {
            if (!state.OpenUntil.HasValue)
            {
                return true;
            }

            if (now < state.OpenUntil.Value)
            {
                return false;
            }

            // Cooldown elapsed: admit exactly one half-open probe by pushing the next probe window out.
            state.OpenUntil = now + Dispatch.CircuitBreakerCooldown;
            return true;
        }
    }

    /// <summary>
    /// Whether the channel is currently tripped open (used by ops-notification composition to skip a
    /// dead channel without minting new dispatch rows). Does not consume a half-open probe.
    /// </summary>
    public bool IsOpen(AlertChannelType channel, DateTimeOffset now)
    {
        if (Dispatch.CircuitBreakerThreshold <= 0)
        {
            return false;
        }

        if (!_states.TryGetValue(channel, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            return state.OpenUntil.HasValue && now < state.OpenUntil.Value;
        }
    }

    /// <summary>The instant a deferred (open) channel becomes eligible for its next probe.</summary>
    public DateTimeOffset NextProbeAt(AlertChannelType channel, DateTimeOffset now)
    {
        if (_states.TryGetValue(channel, out var state))
        {
            lock (state.Sync)
            {
                if (state.OpenUntil.HasValue && state.OpenUntil.Value > now)
                {
                    return state.OpenUntil.Value;
                }
            }
        }

        return now + Dispatch.CircuitBreakerCooldown;
    }

    /// <summary>Records a successful delivery: resets the failure streak and closes the breaker.</summary>
    public void RecordSuccess(AlertChannelType channel)
    {
        if (_states.TryGetValue(channel, out var state))
        {
            lock (state.Sync)
            {
                state.ConsecutiveDeadLetters = 0;
                state.OpenUntil = null;
            }
        }
    }

    /// <summary>
    /// Records a dead-lettered delivery. Trips the breaker open once the consecutive count reaches
    /// the configured threshold. Returns <c>true</c> when this call opened (or re-opened) the breaker.
    /// </summary>
    public bool RecordDeadLetter(AlertChannelType channel, DateTimeOffset now)
    {
        var threshold = Dispatch.CircuitBreakerThreshold;
        if (threshold <= 0)
        {
            return false;
        }

        var state = _states.GetOrAdd(channel, static _ => new ChannelState());
        lock (state.Sync)
        {
            state.ConsecutiveDeadLetters++;
            if (state.ConsecutiveDeadLetters >= threshold)
            {
                state.OpenUntil = now + Dispatch.CircuitBreakerCooldown;
                // Reset the streak so a post-cooldown probe needs a fresh run of failures to re-trip.
                state.ConsecutiveDeadLetters = 0;
                return true;
            }

            return false;
        }
    }

    private sealed class ChannelState
    {
        public object Sync { get; } = new();

        public int ConsecutiveDeadLetters { get; set; }

        public DateTimeOffset? OpenUntil { get; set; }
    }
}
