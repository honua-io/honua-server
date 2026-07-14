// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.CustomCode.Sdk;

/// <summary>
/// Validates artifact/output file names that originate from job parameters. Tools
/// combine these names with <see cref="GpContext.WorkDirectory"/> via
/// <see cref="Path.Combine(string, string)"/>; if a caller-supplied name were rooted
/// (e.g. an absolute path) or escaped the directory (<c>..</c>), the combine would
/// silently discard the work directory and the tool would read/write outside its
/// sandbox. Validate names with this helper before combining.
/// </summary>
public static class ArtifactNames
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is safe to combine
    /// with a base directory: non-empty, contains no path separators, is not
    /// <c>"."</c> or <c>".."</c>, and is not rooted/absolute.
    /// </summary>
    /// <param name="name">The candidate file name, typically read from job parameters.</param>
    public static bool IsSimpleFileName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        name is not ("." or "..") &&
        !name.Contains('/') &&
        !name.Contains('\\') &&
        !Path.IsPathRooted(name);
}
