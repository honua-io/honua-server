// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.ControlPlane;

internal static partial class ComposeExecutorRules
{
    [GeneratedRegex(@"^(?:[a-z0-9][a-z0-9._:/-]{0,240}@)?sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ImmutableImage();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ComposeName();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    internal static partial Regex SqlIdentifier();

    [GeneratedRegex(@"^backup-[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    internal static partial Regex BackupId();

    [GeneratedRegex(@"^[A-Za-z0-9_./-]{1,240}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ScriptName();

    internal static bool IsImmutableImage(string? image)
        => !string.IsNullOrWhiteSpace(image) && ImmutableImage().IsMatch(image);
}
