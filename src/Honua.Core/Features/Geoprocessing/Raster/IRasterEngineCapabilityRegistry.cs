// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Data-driven registry of raster engine capabilities and conservative pre-persistence costs.
/// </summary>
public interface IRasterEngineCapabilityRegistry
{
    /// <summary>Every raster process capability in ordinal process-ID order.</summary>
    IReadOnlyList<RasterProcessCapability> Processes { get; }

    /// <summary>Finds a raster capability by canonical process ID, or returns <c>null</c>.</summary>
    RasterProcessCapability? Find(string processId);

    /// <summary>
    /// Normalizes estimator metadata for a process/engine pair and evaluates the static request
    /// envelope. This does not select an engine or placement.
    /// </summary>
    RasterCostEstimate Estimate(
        string processId,
        RasterEngine engine,
        RasterCostEstimatorInput input);
}
