// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;

namespace Honua.ServiceDefaults;

// When no digest or commit SHA resolves, callers must fail closed instead of substituting the mutable release version.
public static class HonuaDeploymentIdentity
{
    public const string ImageDigestVariable = "HONUA_IMAGE_DIGEST";

    public const string DeploymentRevisionVariable = "HONUA_DEPLOYMENT_REVISION";

    public const string GitShaVariable = "HONUA_GIT_SHA";

    public const string ImageDigestSource = "image-digest";

    public const string CommitShaSource = "commit-sha";

    public const string AssemblyMetadataSource = "assembly-metadata";

    private static readonly (string? Revision, string? Source) Resolved = Resolve();

    public static string? Revision => Resolved.Revision;

    public static string? RevisionSource => Resolved.Source;

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

    public static bool IsCommitSha(string? value) => IsLowerHex(value, 40);

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
