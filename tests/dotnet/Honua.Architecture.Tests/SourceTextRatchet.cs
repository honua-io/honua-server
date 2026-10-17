// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;

namespace Honua.Architecture.Tests;

/// <summary>
/// Shared read and parent-commit comparison for source-text ratchets.
/// A baseline may shrink. It may not gain lines against the checked-out base.
/// The first commit that adds the file is the seed: the parent does not have
/// it yet, so the growth check stays quiet until the file is on trunk.
/// </summary>
internal static class SourceTextRatchet
{
    internal static string AbsolutePath(string repositoryRoot, string relativePath)
        => Path.Join(repositoryRoot, relativePath);

    internal static string[] ReadEntries(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            return [];
        }

        return File.ReadAllLines(absolutePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    /// <summary>
    /// Entries of <paramref name="relativePath"/> on the first parent of HEAD.
    /// Returns null when that parent does not contain the file, which is the
    /// seed commit of the ratchet.
    /// </summary>
    internal static string[]? ReadParentEntries(string repositoryRoot, string relativePath)
    {
        var parent = RunGit(repositoryRoot, "rev-parse", "--verify", "HEAD^1^{commit}").Trim();
        var listed = RunGit(repositoryRoot, "ls-tree", "--name-only", parent, "--", relativePath);
        if (string.IsNullOrWhiteSpace(listed))
        {
            return null;
        }

        return RunGit(repositoryRoot, "show", $"{parent}:{relativePath}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
    }

    internal static string RunGit(string repositoryRoot, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git for a source-text ratchet.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output;
    }
}
