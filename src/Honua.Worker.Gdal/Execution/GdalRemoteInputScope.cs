// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Worker.Gdal.Execution;

// Object-store paths that the GDAL runners may open remotely. Access is granted by
// the executor that built the path with GdalVsiPath.Build, never by a substring of
// the argument vector, so a job value that merely contains "/vsi" cannot re-enable
// remote handlers, project storage credentials or select the remote container
// network. The scope flows with the async execution context, covering every runner
// call made while it is open, and is restored when disposed.
internal static class GdalRemoteInputScope
{
    private const string S3Prefix = "/vsis3/";
    private const string AzurePrefix = "/vsiaz/";

    private static readonly AsyncLocal<ImmutableDictionary<string, GdalRemoteInputs>?> Trusted = new();

    // Null, blank and non-object-store paths (for example a Local provider file) are ignored.
    public static IDisposable Trust(params string?[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var previous = Trusted.Value;
        var next = previous ?? ImmutableDictionary.Create<string, GdalRemoteInputs>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var access = AccessFor(path);
            if (access.Remote)
            {
                next = next.SetItem(path!, access);
            }
        }

        Trusted.Value = next;
        return new Restore(previous);
    }

    // Gives Zarr subdataset names derived from a trusted root the root's access. A root
    // that is not trusted in the current scope trusts nothing, and an argument that
    // does not start with the quoted root after the ZARR prefix is ignored.
    public static IDisposable TrustDerived(IEnumerable<string> arguments, string trustedRoot)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        var previous = Trusted.Value;
        if (previous is not null && previous.TryGetValue(trustedRoot, out var access))
        {
            var next = previous;
            var derivedPrefix = $"ZARR:\"{trustedRoot}\":";
            foreach (var argument in arguments)
            {
                if (!string.IsNullOrEmpty(argument) && argument.StartsWith(derivedPrefix, StringComparison.Ordinal))
                {
                    next = next.SetItem(argument, access);
                }
            }

            Trusted.Value = next;
        }

        return new Restore(previous);
    }

    // Only an argument that is exactly a trusted path counts.
    public static GdalRemoteInputs Classify(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var trusted = Trusted.Value;
        if (trusted is null || trusted.IsEmpty)
        {
            return GdalRemoteInputs.None;
        }

        var s3 = false;
        var azure = false;
        foreach (var argument in arguments)
        {
            if (argument is not null && trusted.TryGetValue(argument, out var access))
            {
                s3 |= access.S3;
                azure |= access.Azure;
            }
        }

        return new GdalRemoteInputs(s3 || azure, s3, azure);
    }

    private static GdalRemoteInputs AccessFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return GdalRemoteInputs.None;
        }

        var s3 = path.StartsWith(S3Prefix, StringComparison.Ordinal);
        var azure = path.StartsWith(AzurePrefix, StringComparison.Ordinal);
        return new GdalRemoteInputs(s3 || azure, s3, azure);
    }

    private sealed class Restore(ImmutableDictionary<string, GdalRemoteInputs>? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Trusted.Value = previous;
        }
    }
}

internal readonly record struct GdalRemoteInputs(bool Remote, bool S3, bool Azure)
{
    public static GdalRemoteInputs None => default;
}
