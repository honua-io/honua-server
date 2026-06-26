// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Shared, production-safe redaction for worker/scratch filesystem paths that
/// must never reach a client surface. The cross-cutting rule (see the GDAL
/// worker's error-sanitizer and the console job-detail glass-box projection) is
/// that scratch workspace paths — which encode the worker filesystem layout and
/// per-job operation ids — and any other absolute filesystem path are replaced
/// with a stable placeholder before the text is persisted onto a job's
/// client-visible <c>ErrorMessage</c> or returned over an admin HTTP surface.
/// Full unredacted text is preserved in structured worker logs for diagnosis.
/// </summary>
/// <remarks>
/// This is the single source of truth shared by the GDAL worker
/// (<c>GdalErrorSanitizer</c>, which delegates here so its production behaviour is
/// unchanged) and the console job glass-box projection. Keeping one
/// implementation guarantees the HTTP surface redacts exactly what the worker
/// already redacts onto <c>ErrorMessage</c>, plus a defensive sweep for any
/// residual absolute paths in log messages/metadata the worker did not
/// pre-sanitize.
/// </remarks>
public static partial class WorkspacePathSanitizer
{
    /// <summary>
    /// Placeholder substituted for the per-job scratch workspace path.
    /// </summary>
    public const string ScratchPlaceholder = "<scratch>";

    /// <summary>
    /// Placeholder substituted for any other absolute filesystem path during the
    /// defensive sweep.
    /// </summary>
    public const string PathPlaceholder = "<path>";

    /// <summary>
    /// Default maximum length of a sanitized string. Matches the GDAL worker's
    /// client-visible error-message ceiling.
    /// </summary>
    public const int DefaultMaxLength = 500;

    // Matches POSIX absolute paths (/a/b/c) and Windows absolute paths
    // (C:\a\b or \\host\share\a). The leading negative lookbehind `(?<![>\w])`
    // skips a path segment that directly follows a placeholder (e.g. the
    // "/input.tif" tail after "<scratch>") so the readable <scratch>/<file>
    // form survives the targeted workspace replacement, while a residual,
    // standalone absolute path is still redacted. Bounded character classes keep
    // the regex linear and AOT-safe (source-generated, no runtime compilation).
    [GeneratedRegex(
        @"(?<![>\w])(?:[A-Za-z]:[\\/]|\\\\[^\\/\s]+[\\/]|/)[^\s""'<>|]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathRegex();

    /// <summary>
    /// Redacts the known per-job <paramref name="workspace"/> path (replaced with
    /// <see cref="ScratchPlaceholder"/>) and length-caps the result. This is the
    /// behaviour the GDAL worker relies on for its client-visible error message:
    /// callers see logical labels (<c>&lt;scratch&gt;/input.tif</c>) instead of the
    /// real per-job directory.
    /// </summary>
    /// <param name="text">Raw text to sanitize (typically GDAL stderr).</param>
    /// <param name="workspace">Per-job scratch workspace path to redact, or empty when unknown.</param>
    /// <param name="maxLength">Maximum length of the returned string.</param>
    public static string Sanitize(string? text, string? workspace, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var redacted = string.IsNullOrEmpty(workspace)
            ? trimmed
            : trimmed.Replace(workspace, ScratchPlaceholder, StringComparison.Ordinal);

        return Truncate(redacted, maxLength);
    }

    /// <summary>
    /// Defensively redacts the known per-job <paramref name="workspace"/> path AND
    /// any residual absolute filesystem path (replaced with <see cref="PathPlaceholder"/>)
    /// before length-capping. Used by client-facing HTTP surfaces (the console
    /// job glass-box) where the source text — execution-log messages and metadata —
    /// may contain paths the worker did not pre-sanitize, so a missed path must
    /// never leak the worker filesystem layout to an operator UI.
    /// </summary>
    /// <param name="text">Raw text to sanitize.</param>
    /// <param name="workspace">Per-job scratch workspace path to redact, or empty when unknown.</param>
    /// <param name="maxLength">Maximum length of the returned string.</param>
    public static string SanitizeForClient(string? text, string? workspace, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var redacted = string.IsNullOrEmpty(workspace)
            ? trimmed
            : trimmed.Replace(workspace, ScratchPlaceholder, StringComparison.Ordinal);

        redacted = AbsolutePathRegex().Replace(redacted, PathPlaceholder);

        return Truncate(redacted, maxLength);
    }

    /// <summary>
    /// Trims and length-caps text WITHOUT redacting paths — for the worker's
    /// operator-facing structured log, where scratch paths are intentionally
    /// preserved for diagnosis and only the length ceiling applies.
    /// </summary>
    /// <param name="text">Raw text to truncate.</param>
    /// <param name="maxLength">Maximum length of the returned string.</param>
    public static string TruncateForLog(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return Truncate(trimmed, maxLength);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
