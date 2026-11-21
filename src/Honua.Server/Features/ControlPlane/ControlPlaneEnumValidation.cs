// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// Configuration binding accepts any integer for an enum property, so a retired or mistyped
// numeric value would otherwise bind to an undefined member and fail only at dispatch.
internal static class ControlPlaneEnumValidation
{
    public static void Validate(ControlPlaneOptions options, List<string> failures)
    {
        for (var i = 0; i < options.DeployTargets.Count; i++)
        {
            RequireDefined(options.DeployTargets[i].TargetKind, $"DeployTargets:{i}:TargetKind", failures);
        }
    }

    private static void RequireDefined<TEnum>(TEnum value, string property, List<string> failures)
        where TEnum : struct, Enum
    {
        if (Enum.IsDefined(value))
        {
            return;
        }

        failures.Add(
            $"{ControlPlaneOptions.SectionName}:{property} value '{value}' is not a known {typeof(TEnum).Name}. "
            + $"Use one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}
