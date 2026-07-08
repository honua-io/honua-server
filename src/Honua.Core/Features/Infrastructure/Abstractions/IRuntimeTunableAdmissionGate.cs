// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Runtime-tunable bounded database admission gate. The configured admission
/// limits are captured at construction time; this seam exposes a bounded-range,
/// transient override so a control-plane ops action can relieve database pressure
/// by lowering the concurrent-query admission target without a restart. The
/// override is never persisted: on process restart the gate reverts to its
/// configured values.
/// </summary>
public interface IRuntimeTunableAdmissionGate
{
    /// <summary>
    /// Current logical concurrent-query admission target.
    /// </summary>
    int CurrentLimit { get; }

    /// <summary>
    /// Hard ceiling for the admission target. A tune request may not exceed this
    /// value (the gate can never admit more than the configured pool can serve).
    /// </summary>
    int MaxLimit { get; }

    /// <summary>
    /// Lowest admission target a tune request may set (inclusive).
    /// </summary>
    int MinLimit { get; }

    /// <summary>
    /// Attempts to set a new transient admission target, validated against the
    /// bounded range <c>[<see cref="MinLimit"/>, <see cref="MaxLimit"/>]</c>. The
    /// value is not persisted and reverts to the configured limit on restart.
    /// </summary>
    /// <param name="limit">Requested admission target.</param>
    /// <param name="error">Human-readable rejection reason when the value is out of range.</param>
    /// <returns>True when the target was applied; false when <paramref name="limit"/> is out of range.</returns>
    bool TrySetLimit(int limit, out string? error);
}
