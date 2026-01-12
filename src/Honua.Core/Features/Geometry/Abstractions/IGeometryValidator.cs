// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Domain;

namespace Honua.Core.Features.Geometry.Abstractions;

/// <summary>
/// Provides geometry validation and repair capabilities.
/// Implements three-layer validation: input format, WKB structure, and topology.
/// </summary>
public interface IGeometryValidator
{
    /// <summary>
    /// Validates Esri JSON geometry structure and coordinates.
    /// Layer 1 validation: input format validation.
    /// </summary>
    /// <param name="geometry">The geometry object to validate.</param>
    /// <returns>Validation result with any errors or warnings.</returns>
    GeometryValidationResult ValidateEsriJson(object? geometry);

    /// <summary>
    /// Validates WKB binary structure and size limits.
    /// Layer 2 validation: binary format validation.
    /// </summary>
    /// <param name="wkb">The WKB bytes to validate.</param>
    /// <returns>Validation result with any errors or warnings.</returns>
    GeometryValidationResult ValidateWkb(byte[]? wkb);

    /// <summary>
    /// Validates geometry topology using the configured topology validator.
    /// Layer 3 validation: topological validation.
    /// </summary>
    /// <param name="wkb">The WKB bytes to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with topology details.</returns>
    Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to repair an invalid geometry.
    /// </summary>
    /// <param name="wkb">The invalid WKB bytes to repair.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Repair result with repaired geometry if successful.</returns>
    Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs complete validation pipeline including all three layers.
    /// Automatically repairs geometries when configured.
    /// </summary>
    /// <param name="wkb">The WKB bytes to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Complete validation result with repaired geometry if applicable.</returns>
    Task<GeometryValidationResult> ValidateCompleteAsync(byte[] wkb, CancellationToken cancellationToken = default);
}
