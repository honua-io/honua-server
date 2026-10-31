// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.ControlPlane;

/// <summary>
/// Allowlist patterns for the Compose executor. Image digests are lowercase hex with no
/// <see cref="RegexOptions.IgnoreCase"/>, so a mixed-case digest is rejected.
/// </summary>
internal static partial class ComposeExecutorRules
{
    /// <summary>
    /// Local config id (<c>sha256:&lt;64 hex&gt;</c>) or a name pinned to a digest. Tags without a
    /// digest are rejected.
    /// </summary>
    [GeneratedRegex(@"^(?:[a-z0-9][a-z0-9._:/-]{0,240}@)?sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ImmutableImage();

    /// <summary>Compose project and service names the executor will pass on an argv.</summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ComposeName();

    /// <summary>SQL identifiers interpolated into the fixed psql statements.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    internal static partial Regex SqlIdentifier();

    /// <summary>Backup directory name. Matches <c>backup-{guid:N}</c> in lowercase.</summary>
    [GeneratedRegex(@"^backup-[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    internal static partial Regex BackupId();

    /// <summary>Whether <paramref name="image"/> is an immutable digest reference.</summary>
    internal static bool IsImmutableImage(string? image)
        => !string.IsNullOrWhiteSpace(image) && ImmutableImage().IsMatch(image);
}
