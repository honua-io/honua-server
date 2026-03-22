// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Licensing.Abstractions;

/// <summary>
/// Manages server license state, including validation and hot-reload.
/// </summary>
public interface ILicenseManager
{
    /// <summary>
    /// Gets the current license information.
    /// </summary>
    Task<LicenseInfo> GetLicenseInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the full entitlement inventory with active/inactive state.
    /// </summary>
    Task<IReadOnlyList<Entitlement>> GetEntitlementsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads and applies a new license file without requiring a server restart.
    /// </summary>
    /// <param name="licenseData">Raw license file content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated license information after applying the new license.</returns>
    Task<LicenseInfo> ApplyLicenseAsync(byte[] licenseData, CancellationToken cancellationToken = default);
}
