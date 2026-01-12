// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Domain;

namespace Honua.Core.Features.Geometry.Abstractions;

/// <summary>
/// Provides topology validation and repair capabilities for geometries.
/// </summary>
public interface IGeometryTopologyValidator
{
    /// <summary>
    /// Validates geometry topology.
    /// </summary>
    /// <param name="wkb">The WKB bytes to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with topology details.</returns>
    Task<GeometryValidationResult> ValidateTopologyAsync(
        byte[] wkb,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to repair an invalid geometry.
    /// </summary>
    /// <param name="wkb">The invalid WKB bytes to repair.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Repair result with repaired geometry if successful.</returns>
    Task<GeometryRepairResult> RepairAsync(
        byte[] wkb,
        CancellationToken cancellationToken = default);
}
