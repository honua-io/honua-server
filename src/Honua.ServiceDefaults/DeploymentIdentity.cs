// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.ServiceDefaults;

// An unparseable revision becomes null so a public metadata surface never presents free text as a verifiable digest.
public sealed class DeploymentIdentity
{
    public const string SectionName = "Deployment";

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

    // The release version can be shared by different builds, so evidence must not be bound to it.
    public string ReleaseVersion { get; }

    public string? Revision { get; }

    public string? RevisionSource { get; }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
