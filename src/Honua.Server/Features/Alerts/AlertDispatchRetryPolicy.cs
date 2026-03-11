// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Server.Features.Alerts;

internal static class AlertDispatchRetryPolicy
{
    public static DateTimeOffset ComputeNextAttempt(
        int attemptNumber,
        DateTimeOffset now,
        AlertDispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var exponent = Math.Clamp(attemptNumber - 1, 0, 10);
        var multiplier = 1 << exponent;

        var rawDelay = TimeSpan.FromTicks(options.InitialBackoff.Ticks * multiplier);
        var delay = rawDelay > options.MaxBackoff
            ? options.MaxBackoff
            : rawDelay;

        return now + delay;
    }
}
