// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Sanitizes GDAL CLI stderr before it is persisted onto a job's
/// client-visible <c>ErrorMessage</c>. The shared cross-cutting rule is that
/// failure messages must not leak scratch workspace paths (which encode the
/// worker filesystem layout and job ids) or other internal directories.
/// Full unsanitized stderr is preserved in structured logs at the executor's
/// <c>Log.ToolFailed</c> call so diagnosis is still possible.
/// </summary>
internal static class GdalErrorSanitizer
{
    private const int MaxLength = 500;
    private const string ScratchPlaceholder = "<scratch>";

    /// <summary>
    /// Returns a sanitized, length-capped stderr suitable for the job's
    /// client-visible error message. Replaces the per-job workspace path with
    /// a stable placeholder so callers see logical labels (<c>&lt;scratch&gt;/input.tif</c>)
    /// instead of <c>/tmp/honua-gdal-worker/&lt;opId&gt;/input.tif</c>.
    /// </summary>
    public static string Sanitize(string stderr, string workspace)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return string.Empty;
        }

        var trimmed = stderr.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var redacted = string.IsNullOrEmpty(workspace)
            ? trimmed
            : trimmed.Replace(workspace, ScratchPlaceholder, StringComparison.Ordinal);

        return redacted.Length <= MaxLength ? redacted : redacted[..MaxLength] + "…";
    }

    /// <summary>
    /// Trims and length-caps unredacted stderr for the executor's operator-facing
    /// structured log. Unlike <see cref="Sanitize"/> this preserves scratch paths
    /// (operators need them for diagnosis); only the length ceiling is applied.
    /// Centralized so every executor's <c>Log.ToolFailed</c> call shares one cap.
    /// </summary>
    public static string TruncateForLog(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return string.Empty;
        }

        var trimmed = stderr.Trim();
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength] + "…";
    }
}
