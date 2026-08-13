// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Configuration for the geoprocessing output staging store (#3089). Bound by both the
/// serving host (result download, registration, orphan sweeping) and the GDAL worker
/// host (attempt-scoped staging writes) from the same section so descriptors written by
/// one side resolve on the other. Staging is opt-in: when disabled, workers keep the
/// legacy bounded inline publication path.
/// </summary>
/// <remarks>
/// OPERATIONAL REQUIREMENT: this section must be configured IDENTICALLY (enabled state,
/// provider, <see cref="StoreReference"/>, and — for the local provider — a shared
/// <see cref="LocalRootPath"/> volume) on every host that runs the GDAL worker or serves
/// results. A worker-enabled/server-disabled or mismatched-store split means the serving
/// host cannot stream staged artifacts: result links are then withheld (surfaced as
/// unavailable, logged per artifact), registration retention holds cannot be written,
/// and no orphan sweeper reclaims that store.
/// </remarks>
public sealed class GeoprocessingOutputStagingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Geoprocessing:OutputStaging";

    /// <summary>Provider value for the shared-filesystem store.</summary>
    public const string LocalProvider = "local";

    /// <summary>Whether referenced output staging is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Staging store provider. This release implements <see cref="LocalProvider"/>
    /// (a filesystem root shared between the worker and serving hosts); an
    /// unimplemented provider fails registration closed with an actionable error.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; set; } = LocalProvider;

    /// <summary>
    /// Logical store identity recorded on descriptors. Never a URI, path, or credential.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$",
        ErrorMessage = "StoreReference must be an opaque identifier")]
    public string StoreReference { get; set; } = "gp-outputs";

    /// <summary>
    /// Filesystem root for the local provider. Both hosts must see the same directory
    /// (a shared volume in containerized deployments).
    /// </summary>
    public string? LocalRootPath { get; set; }

    /// <summary>Key prefix staged outputs are written under.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(?!.*\\.\\.)[A-Za-z0-9][A-Za-z0-9._/-]{0,158}$",
        ErrorMessage = "KeyPrefix must be a bounded relative key prefix without traversal segments")]
    public string KeyPrefix { get; set; } = "gp/outputs";

    /// <summary>
    /// Ceiling for outputs that remain inline in the durable record when staging is
    /// enabled. Anything larger is staged by reference. Clamped to the
    /// <see cref="RasterOutputContract.MaximumInlinePayloadBytes"/> contract ceiling.
    /// </summary>
    [Range(1024, RasterOutputContract.MaximumInlinePayloadBytes,
        ErrorMessage = "MaxInlineArtifactBytes must be between 1 KiB and the 8 MiB contract ceiling")]
    public int MaxInlineArtifactBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Read lease granted to a download stream so the sweeper cannot delete it mid-read.</summary>
    public TimeSpan ReadLeaseDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Interval between orphan sweeps.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Minimum object age before the sweeper may act on it. Protects objects that are
    /// mid-publication (staged but not yet referenced by a durable publish).
    /// </summary>
    public TimeSpan SweepGrace { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Retention for staged objects whose job record no longer exists (expired from the
    /// job store). Should be at least the geoprocessing result retention.
    /// </summary>
    public TimeSpan OrphanRetention { get; set; } = TimeSpan.FromDays(7);
}
