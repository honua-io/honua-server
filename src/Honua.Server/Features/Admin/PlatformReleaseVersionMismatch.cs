// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

// The server never resolves a release version to an artifact; a requested version that is not the configured release is rejected.
internal static class PlatformReleaseVersionMismatch
{
    internal const string Code = "platform_release_version_mismatch";

    internal const string Title = "Platform release version mismatch";

    internal static string Describe(string targetVersion, string declaredVersion)
        => $"Requested platform release '{targetVersion}' does not match the declared release '{declaredVersion}'. "
            + "This server converges only to its declared ControlPlane:PlatformRelease; re-provision with the "
            + "target release lock to change the declared release, then propose convergence again.";
}
