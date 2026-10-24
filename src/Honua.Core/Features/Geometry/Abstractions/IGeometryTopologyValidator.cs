// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

/// <summary>
/// Validates an ordered set of geometries without opening a database connection for each one.
/// A provider that cannot preserve one result per input should fail the whole probe.
/// </summary>
public interface IBatchGeometryTopologyValidator
{
    /// <summary>Returns topology results in the same order as the input WKB values.</summary>
    Task<IReadOnlyList<GeometryValidationResult>> ValidateTopologiesAsync(
        IReadOnlyList<byte[]> wkbs,
        CancellationToken cancellationToken = default);
}
