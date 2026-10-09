// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

/// <summary>
/// Typed rejection for a platform-release convergence whose requested <c>targetVersion</c> does not
/// name the release declared in <c>ControlPlane:PlatformRelease</c> (rc.3 fix unit S3, ruling R13).
/// The server never resolves a version to an artifact itself: version-to-digest resolution lives in the
/// signed release lock, which the provisioning executor writes into <c>ControlPlane__PlatformRelease__*</c>.
/// Surfaced identically by the MCP proposal tool (<c>outcome: rejected</c>) and the REST converge route
/// (409 problem).
/// </summary>
internal static class PlatformReleaseVersionMismatch
{
    /// <summary>Stable machine-readable rejection code surfaced by REST and MCP.</summary>
    internal const string Code = "platform_release_version_mismatch";

    internal const string Title = "Platform release version mismatch";

    internal static string Describe(string targetVersion, string declaredVersion)
        => $"Requested platform release '{targetVersion}' does not match the declared release '{declaredVersion}'. "
            + "This server converges only to its declared ControlPlane:PlatformRelease; re-provision with the "
            + "target release lock to change the declared release, then propose convergence again.";
}
