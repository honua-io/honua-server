// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Canonical semantics for the job runtime-profile claim fence.
/// </summary>
/// <remarks>
/// The server runs a lean serving image (GDAL-free, fast cold-start) that executes
/// <see cref="Managed"/>-profile jobs, and — separately — a heavyweight GDAL worker
/// that executes <see cref="Native"/>-profile jobs. The job queue's claim path uses
/// these helpers to guarantee that a worker only ever claims a job whose effective
/// runtime profile is in the worker's accepted set: the lean worker never claims a
/// <c>native</c> job, and the native worker only claims <c>native</c> jobs.
///
/// <para>
/// SAFE (fail-closed) semantics: a job whose <c>RuntimeProfile</c> is <c>null</c> or
/// empty is treated as the <see cref="Managed"/> default; an executor/worker that does
/// not declare an accepted set is fenced to <see cref="DefaultAccepted"/>
/// (managed-only) rather than "accepts any". This is the deliberate reversal of the
/// earlier footgun where an unset accepted-set meant "accept everything", which would
/// have let the lean worker claim native GDAL jobs.
/// </para>
/// </remarks>
public static class RuntimeProfiles
{
    /// <summary>
    /// The managed/default runtime profile. A job that carries no explicit
    /// <c>RuntimeProfile</c> (null or empty) is treated as running under this profile.
    /// </summary>
    public const string Managed = "managed";

    /// <summary>
    /// The heavyweight native runtime profile served by the GDAL worker image.
    /// Defined here for shared reference; the GDAL worker itself is added in a later
    /// stream and is not part of this substrate.
    /// </summary>
    public const string Native = "native";

    /// <summary>
    /// The accepted-profile set applied to any executor/worker that does not override
    /// it: managed/default jobs only. This keeps every existing trunk executor — and
    /// the lean dispatcher composed from them — fenced so it can never claim a
    /// <see cref="Native"/> job.
    /// </summary>
    public static IReadOnlySet<string> DefaultAccepted { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Managed };

    /// <summary>
    /// Normalizes a job's raw <c>RuntimeProfile</c> string to its effective profile:
    /// a null or whitespace-only value is the <see cref="Managed"/> default; any other
    /// value is returned verbatim.
    /// </summary>
    /// <param name="runtimeProfile">The raw runtime-profile value from the job spec.</param>
    /// <returns>The effective runtime profile, never null or empty.</returns>
    public static string Normalize(string? runtimeProfile)
        => string.IsNullOrWhiteSpace(runtimeProfile) ? Managed : runtimeProfile;

    /// <summary>
    /// Determines whether a worker that accepts <paramref name="acceptedProfiles"/>
    /// may claim a job whose raw runtime profile is <paramref name="jobRuntimeProfile"/>.
    /// </summary>
    /// <param name="acceptedProfiles">
    /// The set of profiles the worker accepts. A <c>null</c> set is treated as
    /// <see cref="DefaultAccepted"/> (managed-only) — NOT "accept any" — so an
    /// undeclared worker is fenced to the managed default.
    /// </param>
    /// <param name="jobRuntimeProfile">The raw <c>RuntimeProfile</c> from the job spec.</param>
    /// <returns>
    /// <c>true</c> when the job's effective profile is contained in the worker's
    /// effective accepted set; otherwise <c>false</c>.
    /// </returns>
    public static bool CanClaim(IReadOnlySet<string>? acceptedProfiles, string? jobRuntimeProfile)
    {
        var effectiveAccepted = acceptedProfiles ?? DefaultAccepted;
        var effectiveJobProfile = Normalize(jobRuntimeProfile);
        return effectiveAccepted.Contains(effectiveJobProfile);
    }
}
