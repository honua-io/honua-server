// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

// The override is not persisted, and the gate returns to its configured limit on restart.
public interface IRuntimeTunableAdmissionGate
{
    int CurrentLimit { get; }

    // A tune request cannot raise the target above what the configured pool can serve.
    int MaxLimit { get; }

    int MinLimit { get; }

    bool TrySetLimit(int limit, out string? error);

    DatabaseAdmissionPressure GetPressure();
}

// The timeout count is a trailing window, so an old exhaustion episode clears instead of accumulating for the life of the process.
public readonly record struct DatabaseAdmissionPressure(
    int InFlight,
    int CurrentLimit,
    int QueuedWaiters,
    double QueueWaitEwmaMs,
    long WindowedAcquisitionTimeouts)
{
    public bool HasUtilization => CurrentLimit > 0;

    public double Utilization => CurrentLimit > 0 ? (double)InFlight / CurrentLimit : 0d;
}
