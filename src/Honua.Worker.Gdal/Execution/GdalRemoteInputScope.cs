// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Executor-declared set of object-store paths (<c>/vsis3/…</c>, <c>/vsiaz/…</c>)
/// that the GDAL runners may open remotely. Remote access is decided by the
/// executor that built the path with <see cref="GdalVsiPath.Build"/>, never by a
/// substring of the argument vector: a job-supplied value such as a layer name
/// that happens to contain <c>/vsi</c> cannot re-enable remote handlers,
/// project storage credentials or select the remote container network.
/// </summary>
/// <remarks>
/// The scope flows with the async execution context, so it covers every runner
/// call made (directly or through shared helpers) while it is open, and is
/// restored when disposed.
/// </remarks>
internal static class GdalRemoteInputScope
{
    private const string S3Prefix = "/vsis3/";
    private const string AzurePrefix = "/vsiaz/";

    private static readonly AsyncLocal<ImmutableDictionary<string, GdalRemoteInputs>?> Trusted = new();

    /// <summary>
    /// Trusts the supplied paths for remote access until the returned handle is
    /// disposed. Null, blank and non-object-store paths (for example a Local
    /// provider file path) are ignored.
    /// </summary>
    /// <param name="paths">Paths built by <see cref="GdalVsiPath.Build"/>.</param>
    /// <returns>A handle that restores the previous scope.</returns>
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

    /// <summary>
    /// Trusts arguments GDAL derived from an already trusted root (for example
    /// <c>ZARR:"/vsis3/…":/array</c> subdataset names) with the root's access. A
    /// root that is not trusted in the current scope trusts nothing, and an
    /// argument that does not contain the root is ignored.
    /// </summary>
    /// <param name="arguments">The exact derived arguments.</param>
    /// <param name="trustedRoot">The trusted path they were derived from.</param>
    /// <returns>A handle that restores the previous scope.</returns>
    public static IDisposable TrustDerived(IEnumerable<string> arguments, string trustedRoot)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        var previous = Trusted.Value;
        if (previous is not null && previous.TryGetValue(trustedRoot, out var access))
        {
            var next = previous;
            foreach (var argument in arguments)
            {
                if (!string.IsNullOrEmpty(argument) && argument.Contains(trustedRoot, StringComparison.Ordinal))
                {
                    next = next.SetItem(argument, access);
                }
            }

            Trusted.Value = next;
        }

        return new Restore(previous);
    }

    /// <summary>
    /// Classifies an argument vector against the current scope. Only an argument
    /// that is exactly a trusted path counts.
    /// </summary>
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

/// <summary>
/// Remote object-store access an invocation is allowed, derived from
/// <see cref="GdalRemoteInputScope"/>.
/// </summary>
/// <param name="Remote">Whether any trusted remote path is an argument.</param>
/// <param name="S3">Whether a trusted <c>/vsis3/</c> path is an argument.</param>
/// <param name="Azure">Whether a trusted <c>/vsiaz/</c> path is an argument.</param>
internal readonly record struct GdalRemoteInputs(bool Remote, bool S3, bool Azure)
{
    /// <summary>No remote access.</summary>
    public static GdalRemoteInputs None => default;
}
