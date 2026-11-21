// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.ControlPlane;

// Full ECS task definition revision ARNs: arn:{partition}:ecs:{region}:{account}:task-definition/{family}:{revision}.
// A family fixes the task role, execution role and network mode, so moving a service between
// families, accounts or regions is not a revision change.
internal static partial class EcsTaskDefinitionRevision
{
    public static bool IsRevisionArn(string? value) => FamilyScope(value) is not null;

    public static bool AreSameFamily(string? candidate, string? reference)
        => FamilyScope(candidate) is { } scope
            && string.Equals(scope, FamilyScope(reference), StringComparison.Ordinal);

    private static string? FamilyScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = RevisionArnPattern().Match(value.Trim());
        return match.Success ? match.Groups["scope"].Value : null;
    }

    [GeneratedRegex(
        @"^arn:(?<scope>aws[a-z-]*:ecs:[a-z0-9-]+:\d{12}:task-definition/[A-Za-z0-9_-]{1,255}):[1-9]\d{0,9}$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex RevisionArnPattern();
}
