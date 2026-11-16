// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Geoprocessing.Domain;

public static partial class WorkspacePathSanitizer
{
    public const string ScratchPlaceholder = "<scratch>";

    public const string PathPlaceholder = "<path>";

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

    [GeneratedRegex(
        @"\b(?:Bearer|Basic)\s+[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"\b((?:[\w-]*(?:password|passwd|secret|token|api[_-]?key|access[_-]?key)[\w-]*|pwd)[""']?\s*(?:=|:|\s)\s*)(?:""(?:\\.|[^""\\])*(?:""|\\?$)|'(?:\\.|[^'\\])*(?:'|\\?$)|[^\s,;""'<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CredentialValueRegex();

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

        // Run before path replacement so URL/path structure cannot hide a credential.
        // This covers recognizable credentials, not arbitrary sensitive user data.
        try
        {
            redacted = AuthorizationRegex().Replace(redacted, "[REDACTED]");
            redacted = CredentialValueRegex().Replace(redacted, "$1[REDACTED]");
            redacted = AbsolutePathRegex().Replace(redacted, PathPlaceholder);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED]";
        }

        return Truncate(redacted, maxLength);
    }

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
