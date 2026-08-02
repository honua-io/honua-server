// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Validates that every available engine capability has a registered process executor route.
/// Unavailable entries are deliberately ignored because their explicit reason is the contract.
/// </summary>
public static class RasterEngineExecutorCoverageValidator
{
    /// <summary>
    /// Throws when an available process/engine capability has no registered executor process ID.
    /// </summary>
    /// <param name="registry">Capability registry to validate.</param>
    /// <param name="engine">Engine owned by the composition root.</param>
    /// <param name="registeredProcessIds">Process IDs derived from registered executors.</param>
    public static void Validate(
        IRasterEngineCapabilityRegistry registry,
        RasterEngine engine,
        IReadOnlySet<string> registeredProcessIds)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(registeredProcessIds);

        var missing = registry.Processes
            .Where(process => process.Engines.Any(capability =>
                capability.Engine == engine && capability.IsAvailable))
            .Select(process => process.ProcessId)
            .Where(processId => !registeredProcessIds.Contains(processId))
            .OrderBy(processId => processId, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Raster engine '{engine}' advertises process IDs with no registered executor: "
                + $"{string.Join(", ", missing)}.");
        }
    }
}
