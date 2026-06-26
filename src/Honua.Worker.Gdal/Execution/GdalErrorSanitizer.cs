// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Sanitizes GDAL CLI stderr before it is persisted onto a job's
/// client-visible <c>ErrorMessage</c>. The shared cross-cutting rule is that
/// failure messages must not leak scratch workspace paths (which encode the
/// worker filesystem layout and job ids) or other internal directories.
/// Full unsanitized stderr is preserved in structured logs at the executor's
/// <c>Log.ToolFailed</c> call so diagnosis is still possible.
/// </summary>
/// <remarks>
/// This is a thin worker-side façade over <see cref="WorkspacePathSanitizer"/>
/// in <c>Honua.Core</c>, which is the single source of truth shared with the
/// console job glass-box HTTP projection. Behaviour is unchanged from when the
/// redaction lived here.
/// </remarks>
internal static class GdalErrorSanitizer
{
    /// <summary>
    /// Returns a sanitized, length-capped stderr suitable for the job's
    /// client-visible error message. Replaces the per-job workspace path with
    /// a stable placeholder so callers see logical labels (<c>&lt;scratch&gt;/input.tif</c>)
    /// instead of <c>/tmp/honua-gdal-worker/&lt;opId&gt;/input.tif</c>.
    /// </summary>
    public static string Sanitize(string stderr, string workspace)
        => WorkspacePathSanitizer.Sanitize(stderr, workspace);

    /// <summary>
    /// Trims and length-caps unredacted stderr for the executor's operator-facing
    /// structured log. Unlike <see cref="Sanitize"/> this preserves scratch paths
    /// (operators need them for diagnosis); only the length ceiling is applied.
    /// Centralized so every executor's <c>Log.ToolFailed</c> call shares one cap.
    /// </summary>
    public static string TruncateForLog(string stderr)
        => WorkspacePathSanitizer.TruncateForLog(stderr);
}
