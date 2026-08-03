// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Geoprocessing;

/// <summary>Operator policy and budget configuration for raster execution planning.</summary>
internal sealed class RasterExecutionPlannerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Geoprocessing:RasterExecution";

    /// <summary>Stable identifier pinned to decisions made with this configuration.</summary>
    [Required]
    public string ConfigurationVersion { get; set; } = "raster-execution-v1";

    /// <summary>Stable operator policy reference pinned to each decision.</summary>
    [Required]
    public string PolicyRef { get; set; } = "raster-default";

    /// <summary>Whether PostGIS is allowed as a raster execution engine.</summary>
    public bool PostgisEnabled { get; set; } = true;

    /// <summary>Whether isolated native GDAL is allowed as a raster execution engine.</summary>
    public bool NativeGdalEnabled { get; set; } = true;

    /// <summary>Whether bounded request execution is allowed by operator policy.</summary>
    public bool RequestExecutionEnabled { get; set; } = true;

    /// <summary>Whether durable PostGIS placement is allowed.</summary>
    public bool DurablePostgisEnabled { get; set; } = true;

    /// <summary>Whether the local isolated native worker is allowed.</summary>
    public bool LocalNativeWorkerEnabled { get; set; } = true;

    /// <summary>Whether configured remote native backends are allowed.</summary>
    public bool RemoteNativeBackendEnabled { get; set; } = true;

    /// <summary>Optional engine that must be used or refused.</summary>
    public RasterEngine? RequiredEngine { get; set; }

    /// <summary>Optional placement that must be used or refused.</summary>
    public RasterExecutionPlacement? RequiredPlacement { get; set; }

    /// <summary>Optional engine preference after capability and locality gates.</summary>
    public RasterEngine? PreferredEngine { get; set; }

    /// <summary>Current configured database raster-lane health.</summary>
    public RasterDatabaseHealth DatabaseHealth { get; set; } = RasterDatabaseHealth.Healthy;

    /// <summary>Stable identifier for the current health snapshot.</summary>
    [Required]
    public string HealthSnapshotVersion { get; set; } = "configured-health-v1";

    /// <summary>Whether the local native worker is operational.</summary>
    public bool LocalNativeWorkerAvailable { get; set; } = true;

    /// <summary>Maximum request-envelope decoded bytes.</summary>
    [Range(1, long.MaxValue)]
    public long MaxRequestDecodedBytes { get; set; } = 64L * 1024L * 1024L;

    /// <summary>Maximum request-envelope scratch bytes.</summary>
    [Range(1, long.MaxValue)]
    public long MaxRequestScratchBytes { get; set; } = 128L * 1024L * 1024L;

    /// <summary>Maximum request-envelope database work units.</summary>
    [Range(1, long.MaxValue)]
    public long MaxRequestDatabaseWork { get; set; } = 10_000_000L;

    /// <summary>Maximum durable PostGIS decoded bytes.</summary>
    [Range(1, long.MaxValue)]
    public long MaxDatabaseDecodedBytes { get; set; } = 2L * 1024L * 1024L * 1024L;

    /// <summary>Maximum durable PostGIS scratch bytes.</summary>
    [Range(1, long.MaxValue)]
    public long MaxDatabaseScratchBytes { get; set; } = 4L * 1024L * 1024L * 1024L;

    /// <summary>Maximum durable PostGIS database work units.</summary>
    [Range(1, long.MaxValue)]
    public long MaxDatabaseWork { get; set; } = 500_000_000L;

    /// <summary>Maximum local-native decoded bytes before remote placement is preferred.</summary>
    [Range(1, long.MaxValue)]
    public long MaxLocalDecodedBytes { get; set; } = 1024L * 1024L * 1024L;

    /// <summary>Maximum local-native scratch bytes before remote placement is preferred.</summary>
    [Range(1, long.MaxValue)]
    public long MaxLocalScratchBytes { get; set; } = 8L * 1024L * 1024L * 1024L;

    /// <summary>Whether every configured enum value belongs to the supported contract.</summary>
    public bool HasDefinedEnumValues() =>
        Enum.IsDefined(DatabaseHealth)
        && (RequiredEngine is not { } requiredEngine || Enum.IsDefined(requiredEngine))
        && (PreferredEngine is not { } preferredEngine || Enum.IsDefined(preferredEngine))
        && (RequiredPlacement is not { } requiredPlacement || Enum.IsDefined(requiredPlacement));

    /// <summary>Builds the immutable budget snapshot persisted with a decision.</summary>
    public RasterExecutionBudgetSnapshot ToBudgetSnapshot() => new()
    {
        Version = ConfigurationVersion,
        MaxRequestDecodedBytes = MaxRequestDecodedBytes,
        MaxRequestScratchBytes = MaxRequestScratchBytes,
        MaxRequestDatabaseWork = MaxRequestDatabaseWork,
        MaxDatabaseDecodedBytes = MaxDatabaseDecodedBytes,
        MaxDatabaseScratchBytes = MaxDatabaseScratchBytes,
        MaxDatabaseWork = MaxDatabaseWork,
        MaxLocalDecodedBytes = MaxLocalDecodedBytes,
        MaxLocalScratchBytes = MaxLocalScratchBytes,
    };

    /// <summary>Builds the immutable operator policy snapshot persisted with a decision.</summary>
    public RasterExecutionPolicySnapshot ToPolicySnapshot()
    {
        var engines = new List<RasterEngine>(2);
        if (PostgisEnabled)
        {
            engines.Add(RasterEngine.Postgis);
        }

        if (NativeGdalEnabled)
        {
            engines.Add(RasterEngine.GdalNative);
        }

        var placements = new List<RasterExecutionPlacement>(4);
        if (RequestExecutionEnabled)
        {
            placements.Add(RasterExecutionPlacement.Request);
        }

        if (DurablePostgisEnabled)
        {
            placements.Add(RasterExecutionPlacement.DurablePostgis);
        }

        if (LocalNativeWorkerEnabled)
        {
            placements.Add(RasterExecutionPlacement.LocalNativeWorker);
        }

        if (RemoteNativeBackendEnabled)
        {
            placements.Add(RasterExecutionPlacement.RemoteBackend);
        }

        return new RasterExecutionPolicySnapshot
        {
            PolicyRef = PolicyRef,
            AllowedEngines = engines.AsReadOnly(),
            AllowedPlacements = placements.AsReadOnly(),
            RequiredEngine = RequiredEngine,
            RequiredPlacement = RequiredPlacement,
            PreferredEngine = PreferredEngine,
        };
    }
}
