// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AnalysisContent;

/// <summary>
/// Well-known metadata keys used to link geoprocessing jobs and result artifacts
/// back to durable analysis content versions.
/// </summary>
public static class AnalysisContentMetadataKeys
{
    /// <summary>
    /// Source content item identifier.
    /// </summary>
    public const string ItemId = "analysis.content.item_id";

    /// <summary>
    /// Source content item version number.
    /// </summary>
    public const string Version = "analysis.content.version";

    /// <summary>
    /// Source content version identifier.
    /// </summary>
    public const string VersionId = "analysis.content.version_id";

    /// <summary>
    /// Source content kind.
    /// </summary>
    public const string Kind = "analysis.content.kind";

    /// <summary>
    /// Source spatial reference assumption.
    /// </summary>
    public const string SourceSrid = "analysis.content.source_srid";

    /// <summary>
    /// Source unit assumption.
    /// </summary>
    public const string SourceUnits = "analysis.content.source_units";

    /// <summary>
    /// Job identifier that a rerun derives from.
    /// </summary>
    public const string RerunOfJobId = "analysis.content.rerun_of_job_id";

    /// <summary>
    /// Result package identifier that a rerun derives from.
    /// </summary>
    public const string RerunOfResultPackageId = "analysis.content.rerun_of_result_package_id";
}
