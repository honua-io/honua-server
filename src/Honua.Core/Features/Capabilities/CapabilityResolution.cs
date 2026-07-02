// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// The outcome of resolving a capability against a
/// <see cref="CapabilityGateContext"/>. It is the single evaluation result the
/// gate resolver (#2339 / T2), manifest enforcement (T4), and the per-surface
/// gates (T5–T7) consume.
/// </summary>
/// <param name="Enabled">Whether the capability is enabled for the given context.</param>
/// <param name="ReasonCode">
/// A stable machine-readable reason when <paramref name="Enabled"/> is
/// <c>false</c> (for example <c>capability-not-registered</c>), or <c>null</c>
/// when enabled.
/// </param>
public readonly record struct CapabilityResolution(bool Enabled, string? ReasonCode);
