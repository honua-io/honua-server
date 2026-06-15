// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Licensing.Abstractions;

/// <summary>
/// Provides topology-neutral capacity metering and registration admission for license bands.
/// </summary>
public interface ILicenseCapacityMeter
{
    /// <summary>
    /// Registers or refreshes a serving instance in the capacity meter.
    /// </summary>
    /// <param name="request">Registration request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Registration admission decision.</returns>
    Task<LicenseCapacityRegistrationDecision> RegisterInstanceAsync(
        LicenseCapacityRegistrationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current capacity state used by admission and admin dashboards.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Capacity state.</returns>
    Task<LicenseCapacityState> GetCapacityStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates or deactivates declared surge mode.
    /// </summary>
    /// <param name="enabled">Whether surge mode should be active.</param>
    /// <param name="reason">Optional operator-facing reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated capacity state.</returns>
    Task<LicenseCapacityState> SetSurgeModeAsync(
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default);
}
