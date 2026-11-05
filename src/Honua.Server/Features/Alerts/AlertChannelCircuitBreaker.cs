// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

// After consecutive dead-letters the channel is skipped and claimed rows are rescheduled instead of dead-lettered again, and the breaker state is per replica.
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
