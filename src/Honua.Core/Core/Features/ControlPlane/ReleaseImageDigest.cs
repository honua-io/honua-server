// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.ControlPlane;

public static partial class ReleaseImageDigest
{
    public const string NotImmutableCode = "release-image-not-immutable";

    [GeneratedRegex(
        @"^(?<repo>[A-Za-z0-9][A-Za-z0-9._\-]*(?::[0-9]+)?(?:/[A-Za-z0-9._\-]+)+)(?::[A-Za-z0-9._\-]+)?@sha256:(?<digest>[0-9a-f]{64})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ImmutablePattern();

    public static bool IsImmutable(string? value) => TryParse(value, out _, out _);

    public static bool TryParse(string? value, out string repository, out string digest)
    {
        repository = string.Empty;
        digest = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = ImmutablePattern().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        repository = match.Groups["repo"].Value;
        digest = match.Groups["digest"].Value;
        return true;
    }

    public static bool IsEcsTaskDefinitionArn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        return text.StartsWith("arn:", StringComparison.Ordinal)
            && text.Contains(":task-definition/", StringComparison.Ordinal);
    }
}
