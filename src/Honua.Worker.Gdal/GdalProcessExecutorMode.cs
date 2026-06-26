// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal;

/// <summary>
/// Selects how the native GDAL <c>IProcessExecutor</c> set reaches the GDAL/OGR CLI
/// tools when registered through
/// <see cref="GdalWorkerServiceCollectionExtensions.AddGdalProcessExecutors(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration, GdalProcessExecutorMode)"/>
/// (GP Devkit native-profile fidelity, issue #2180).
/// </summary>
public enum GdalProcessExecutorMode
{
    /// <summary>
    /// Default fast path: shell each GDAL tool out to the host's in-process GDAL CLIs.
    /// Keeps the GP Devkit dev loop sub-second; requires GDAL on the host PATH for
    /// native ops (managed ops never touch it).
    /// </summary>
    InProcess = 0,

    /// <summary>
    /// Opt-in fidelity path (<c>--real-worker</c>): run each GDAL tool inside the real
    /// <c>honua-worker-etl</c> container image via <c>docker run</c>, so the image /
    /// CRS-data / driver-set / arg-handling boundary a production native submit crosses
    /// is exercised locally. Requires a container runtime and the worker image.
    /// </summary>
    Container = 1,
}
