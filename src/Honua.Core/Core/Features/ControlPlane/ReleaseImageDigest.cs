// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.ControlPlane;

/// <summary>
/// Immutable container image reference shared by the ECS, Lambda, and Kubernetes release executors.
/// A mutable tag is not a release artifact. The serving artifact is
/// <c>repository@sha256:&lt;64 lowercase hex&gt;</c>, with an optional tag before the digest.
/// </summary>
public static partial class ReleaseImageDigest
{
    /// <summary>Typed refusal code when a caller supplies a tag or other mutable reference.</summary>
    public const string NotImmutableCode = "release-image-not-immutable";

    [GeneratedRegex(
        @"^(?<repo>[A-Za-z0-9][A-Za-z0-9._\-]*(?::[0-9]+)?(?:/[A-Za-z0-9._\-]+)+)(?::[A-Za-z0-9._\-]+)?@sha256:(?<digest>[0-9a-f]{64})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ImmutablePattern();

    /// <summary>Returns true when <paramref name="value"/> is an immutable image digest reference.</summary>
    public static bool IsImmutable(string? value) => TryParse(value, out _, out _);

    /// <summary>Parses an immutable image reference. Tags without a digest return false.</summary>
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

    /// <summary>Returns true when <paramref name="value"/> is an ECS task definition ARN.</summary>
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
