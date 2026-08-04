// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.ServiceDefaults;

/// <summary>
/// Resolved identity of the running deployment, shared by every public metadata surface that
/// has to separate the mutable release version from the immutable code/image revision
/// (#3038).
/// </summary>
/// <remarks>
/// <para>
/// Resolution order: <c>Deployment:ImageDigest</c> configuration, <c>Deployment:Revision</c>
/// configuration, then the process-level environment/assembly resolution in
/// <see cref="HonuaDeploymentIdentity"/>. Configuration comes first so orchestrators that
/// already template values into the deployment manifest (Helm, Terraform, ECS task
/// definitions) do not have to reach for process environment variables, and so tests can
/// exercise the projection deterministically.
/// </para>
/// <para>
/// Values are validated against the commit-SHA and image-digest grammars before being
/// surfaced. An unparseable value resolves to <see langword="null"/> rather than being echoed
/// back: an anonymous discovery surface must never emit a free-text label that consumers
/// could mistake for a verifiable revision.
/// </para>
/// </remarks>
public sealed class DeploymentIdentity
{
    /// <summary>Configuration section holding deployment identity overrides.</summary>
    public const string SectionName = "Deployment";

    /// <summary>
    /// Creates the deployment identity from configuration, falling back to the process
    /// environment and assembly build metadata.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    public DeploymentIdentity(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ReleaseVersion = HonuaDeploymentIdentity.GetReleaseVersion(typeof(DeploymentIdentity).Assembly);

        var section = configuration.GetSection(SectionName);
        var digest = Normalize(section["ImageDigest"]);
        if (HonuaDeploymentIdentity.IsImageDigest(digest))
        {
            Revision = digest;
            RevisionSource = HonuaDeploymentIdentity.ImageDigestSource;
            return;
        }

        var revision = Normalize(section["Revision"]);
        if (HonuaDeploymentIdentity.IsCommitSha(revision))
        {
            Revision = revision;
            RevisionSource = HonuaDeploymentIdentity.CommitShaSource;
            return;
        }

        Revision = HonuaDeploymentIdentity.Revision;
        RevisionSource = HonuaDeploymentIdentity.RevisionSource;
    }

    /// <summary>
    /// Mutable server release version. Two different builds can legitimately advertise the
    /// same value, so it must not be used to bind evidence to a deployment.
    /// </summary>
    public string ReleaseVersion { get; }

    /// <summary>
    /// Immutable deployment revision — a 40-character commit SHA or a
    /// <c>sha256:&lt;64 hex&gt;</c> image digest — or null when the deployment carries none.
    /// </summary>
    public string? Revision { get; }

    /// <summary>How <see cref="Revision"/> was resolved, or null when no revision resolved.</summary>
    public string? RevisionSource { get; }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
