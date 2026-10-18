// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Actual provider output persisted with successful file-import job finalization.
/// This receipt identifies completed output; it does not authorize publication or retry.
/// </summary>
public sealed record FileImportOutputReceipt
{
    /// <summary>Physical table returned by the importer, never reconstructed from the requested name.</summary>
    public required string PhysicalTableName { get; init; }

    /// <summary>Provider schema owning the physical table.</summary>
    public required string Schema { get; init; }

    /// <summary>Number of features reported by the successful importer.</summary>
    public int FeatureCount { get; init; }

    /// <summary>Detected source coordinate reference system, when reported.</summary>
    public int? DetectedSrid { get; init; }
}
