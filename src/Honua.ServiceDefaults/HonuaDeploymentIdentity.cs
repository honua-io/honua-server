// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;

namespace Honua.ServiceDefaults;

/// <summary>
/// Resolves the immutable identity of the running deployment, separately from the
/// mutable release version.
/// </summary>
/// <remarks>
/// <para>
/// The release version (<c>AssemblyInformationalVersion</c>, surfaced as
/// <c>serverVersion</c>) is mutable: two different builds can advertise the same
/// <c>1.0.0</c>. Conformance and evidence consumers need a value that identifies the
/// exact code/image content under review, so retained evidence can be bound to the
/// deployment it was produced against.
/// </para>
/// <para>
/// Resolution order, highest confidence first:
/// </para>
/// <list type="number">
///   <item><description><c>HONUA_IMAGE_DIGEST</c> — the pushed container image digest
///   (<c>sha256:&lt;64 lowercase hex&gt;</c>). Only knowable after the image is pushed,
///   so it is injected at deploy time.</description></item>
///   <item><description><c>HONUA_DEPLOYMENT_REVISION</c> or <c>HONUA_GIT_SHA</c> — the full
///   40-character commit SHA, injected as a build argument by the image build.</description></item>
///   <item><description>SemVer build metadata on the entry assembly's informational version
///   (<c>1.2.3+&lt;sha&gt;</c>), which SourceLink stamps for builds that can see the git
///   repository.</description></item>
/// </list>
/// <para>
/// Values that do not match the strict commit-SHA or image-digest grammar are rejected
/// rather than surfaced, so the field is never a free-text label. When nothing resolves,
/// <see cref="Revision"/> is <see langword="null"/> and callers must fail closed rather
/// than substituting the mutable version.
/// </para>
/// <para>
/// Resolution runs once during static initialization: reflection over assembly attributes
/// is not trim-safe on hot paths, so the result is cached for the process lifetime.
/// </para>
/// </remarks>
public static class HonuaDeploymentIdentity
{
    /// <summary>Environment variable carrying the pushed container image digest.</summary>
    public const string ImageDigestVariable = "HONUA_IMAGE_DIGEST";

    /// <summary>Environment variable carrying an explicit deployment revision (commit SHA).</summary>
    public const string DeploymentRevisionVariable = "HONUA_DEPLOYMENT_REVISION";

    /// <summary>Environment variable carrying the build-time commit SHA.</summary>
    public const string GitShaVariable = "HONUA_GIT_SHA";

    /// <summary><see cref="RevisionSource"/> value for a container image digest.</summary>
    public const string ImageDigestSource = "image-digest";

    /// <summary><see cref="RevisionSource"/> value for an explicitly injected commit SHA.</summary>
    public const string CommitShaSource = "commit-sha";

    /// <summary><see cref="RevisionSource"/> value for a SourceLink-stamped assembly commit SHA.</summary>
    public const string AssemblyMetadataSource = "assembly-metadata";

    private static readonly (string? Revision, string? Source) Resolved = Resolve();

    /// <summary>
    /// The immutable deployment revision: a 40-character lowercase commit SHA, or an
    /// image digest of the form <c>sha256:&lt;64 lowercase hex&gt;</c>. Returns
    /// <see langword="null"/> when the deployment carries no verifiable revision.
    /// </summary>
    public static string? Revision => Resolved.Revision;

    /// <summary>
    /// How <see cref="Revision"/> was resolved: <see cref="ImageDigestSource"/>,
    /// <see cref="CommitShaSource"/>, or <see cref="AssemblyMetadataSource"/>. Returns
    /// <see langword="null"/> when no revision resolved.
    /// </summary>
    public static string? RevisionSource => Resolved.Source;

    /// <summary>
    /// Returns the mutable release version for <paramref name="assembly"/>: its
    /// <see cref="AssemblyInformationalVersionAttribute"/> with any SemVer build metadata
    /// removed, falling back to the assembly version.
    /// </summary>
    /// <param name="assembly">Assembly to read version metadata from.</param>
    /// <returns>The release version string; never empty.</returns>
    public static string GetReleaseVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var release = plus >= 0 ? informational[..plus] : informational;
        return string.IsNullOrWhiteSpace(release)
            ? assembly.GetName().Version?.ToString() ?? "0.0.0"
            : release;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is a full
    /// 40-character lowercase hexadecimal commit SHA.
    /// </summary>
    /// <param name="value">Candidate revision value.</param>
    /// <returns><see langword="true"/> when the value is a valid commit SHA.</returns>
    public static bool IsCommitSha(string? value) => IsLowerHex(value, 40);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is an image digest of
    /// the form <c>sha256:&lt;64 lowercase hex&gt;</c>.
    /// </summary>
    /// <param name="value">Candidate revision value.</param>
    /// <returns><see langword="true"/> when the value is a valid image digest.</returns>
    public static bool IsImageDigest(string? value)
    {
        const string Prefix = "sha256:";
        return value is not null
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && IsLowerHex(value.AsSpan(Prefix.Length), 64);
    }

    private static (string? Revision, string? Source) Resolve()
    {
        var digest = ReadVariable(ImageDigestVariable);
        if (IsImageDigest(digest))
        {
            return (digest, ImageDigestSource);
        }

        var revision = ReadVariable(DeploymentRevisionVariable) ?? ReadVariable(GitShaVariable);
        if (IsCommitSha(revision))
        {
            return (revision, CommitShaSource);
        }

        var stamped = ReadAssemblyBuildMetadata();
        return IsCommitSha(stamped)
            ? (stamped, AssemblyMetadataSource)
            : (null, null);
    }

    private static string? ReadVariable(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
    }

    private static string? ReadAssemblyBuildMetadata()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(HonuaDeploymentIdentity).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || plus == informational.Length - 1)
        {
            return null;
        }

        return informational[(plus + 1)..].Trim().ToLowerInvariant();
    }

    private static bool IsLowerHex(string? value, int length)
        => value is not null && IsLowerHex(value.AsSpan(), length);

    private static bool IsLowerHex(ReadOnlySpan<char> value, int length)
    {
        if (value.Length != length)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
