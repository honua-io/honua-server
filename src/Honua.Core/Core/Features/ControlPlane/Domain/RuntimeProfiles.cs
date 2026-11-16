// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public static class RuntimeProfiles
{
    public const string Managed = "managed";

    public const string Native = "native";

    public static IReadOnlySet<string> DefaultAccepted { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Managed };

    public static string Normalize(string? runtimeProfile)
        => string.IsNullOrWhiteSpace(runtimeProfile) ? Managed : runtimeProfile;

    public static bool CanClaim(IReadOnlySet<string>? acceptedProfiles, string? jobRuntimeProfile)
    {
        var effectiveAccepted = acceptedProfiles ?? DefaultAccepted;
        var effectiveJobProfile = Normalize(jobRuntimeProfile);
        return effectiveAccepted.Contains(effectiveJobProfile);
    }
}
