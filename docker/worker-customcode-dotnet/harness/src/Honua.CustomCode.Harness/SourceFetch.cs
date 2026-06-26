// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.CustomCode.Harness;

/// <summary>Raised when cloning/checkout/verification of user code fails.</summary>
public sealed class SourceFetchException(string message) : Exception(message);

/// <summary>The outcome of running a process: exit code + captured stdout/stderr.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StdOut">Captured standard output.</param>
/// <param name="StdErr">Captured standard error.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Clone + pin user code at an exact git SHA, then verify the checkout. The harness
/// only ever runs code at a fully-pinned 40-hex SHA; after fetching we
/// <c>git rev-parse HEAD</c> and assert it equals the requested SHA. Mirrors the
/// Python harness's <c>sourcefetch.py</c>.
/// </summary>
public sealed class SourceFetch
{
    private readonly Func<IReadOnlyList<string>, string?, ProcessResult> _runner;

    /// <summary>Creates a source fetcher.</summary>
    /// <param name="gitRunner">An injectable git runner (for tests); defaults to invoking the real git binary.</param>
    public SourceFetch(Func<IReadOnlyList<string>, string?, ProcessResult>? gitRunner = null)
    {
        _runner = gitRunner ?? RunGit;
    }

    /// <summary>
    /// Clone <paramref name="repoUrl"/> and check out exactly <paramref name="gitRef"/>
    /// (a 40-hex SHA), then assert <c>rev-parse HEAD == sha</c>.
    /// </summary>
    /// <param name="repoUrl">The git repository URL.</param>
    /// <param name="gitRef">The pinned 40-hex commit SHA.</param>
    /// <param name="destination">The destination directory.</param>
    /// <returns>The destination directory.</returns>
    /// <exception cref="SourceFetchException">On any clone/checkout/verify failure.</exception>
    public string ClonePinned(string repoUrl, string gitRef, string destination)
    {
        if (!JobSpec.IsValidGitSha(gitRef))
        {
            // Defense-in-depth: never pass an unvalidated ref to git.
            throw new SourceFetchException($"refusing to fetch non-SHA git_ref '{gitRef}' (must be 40-hex).");
        }

        Directory.CreateDirectory(destination);
        Git(destination, "init", "--quiet");
        Git(destination, "remote", "add", "origin", repoUrl);

        try
        {
            // A direct fetch of the SHA keeps the download minimal.
            Git(destination, "fetch", "--depth", "1", "origin", gitRef);
        }
        catch (SourceFetchException)
        {
            // Some hosts disable uploadpack.allowReachableSHA1InWant; fall back to a
            // shallow clone of the default branch + a deeper fetch by SHA.
            Git(destination, "fetch", "--depth", "1", "origin");
            Git(destination, "fetch", "--depth", "50", "origin");
        }

        Git(destination, "checkout", "--quiet", "--detach", gitRef);

        var head = Git(destination, "rev-parse", "HEAD").Trim();
        if (!string.Equals(head, gitRef, StringComparison.Ordinal))
        {
            throw new SourceFetchException(
                $"checkout verification failed: HEAD is '{head}' but expected '{gitRef}'.");
        }

        return destination;
    }

    private string Git(string cwd, params string[] args)
    {
        var result = _runner(args, cwd);
        if (result.ExitCode != 0)
        {
            throw new SourceFetchException(
                $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr.Trim()}");
        }

        return result.StdOut ?? string.Empty;
    }

    private static ProcessResult RunGit(IReadOnlyList<string> args, string? cwd)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? string.Empty,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new SourceFetchException("failed to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
