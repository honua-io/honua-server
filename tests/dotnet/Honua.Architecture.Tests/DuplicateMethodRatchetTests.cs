// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ratchet for same-name near-duplicate methods under <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>scripts/ci/duplicate-method-ratchet.py</c> pairs block-bodied methods that
/// share a name, are each at least 25 normalized lines, and whose bodies are at
/// least 0.92 similar after comments and blank lines are removed. The committed
/// baseline is the seed. After it is on trunk the file may only shrink: a new
/// pair fails the set check, and adding a baseline line fails the parent check.
/// Delete a line only when that pair is gone.
/// </para>
/// <para>
/// Regenerate the seed, then delete lines rather than adding them:
/// <c>python3 scripts/ci/duplicate-method-ratchet.py --write tests/dotnet/Honua.Architecture.Tests/duplicate-method-baseline.txt</c>.
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class DuplicateMethodRatchetTests
{
    private const string RelativeBaseline = "tests/dotnet/Honua.Architecture.Tests/duplicate-method-baseline.txt";

    private const string RelativeStaticHelperBaseline =
        "tests/dotnet/Honua.Architecture.Tests/duplicate-static-helper-baseline.txt";

    private const string StaticHelpersFlag = "--static-helpers";

    private const string RelativeScript = "scripts/ci/duplicate-method-ratchet.py";

    [ArchitectureTest]
    public async Task Scanner_SelfTest_Passes()
    {
        var (exitCode, _, error) = await RunAsync("--self-test");
        exitCode.Should().Be(0, "the duplicate-method scanner self-test failed: {0}", error);
    }

    [ArchitectureTest]
    public Task DuplicateMethodPairs_MustMatchTheBaseline()
        => AssertScanMatchesBaselineAsync(RelativeBaseline);

    [ArchitectureTest]
    public Task DuplicateStaticHelperPairs_MustMatchTheBaseline()
        => AssertScanMatchesBaselineAsync(RelativeStaticHelperBaseline, StaticHelpersFlag);

    [ArchitectureTest]
    public void DuplicateMethodBaseline_MustNotGrowFromTheCheckedOutBase()
        => AssertBaselineDidNotGrow(RelativeBaseline);

    [ArchitectureTest]
    public void DuplicateStaticHelperBaseline_MustNotGrowFromTheCheckedOutBase()
        => AssertBaselineDidNotGrow(RelativeStaticHelperBaseline);

    private static async Task AssertScanMatchesBaselineAsync(string relativeBaseline, params string[] arguments)
    {
        var (exitCode, output, error) = await RunAsync(arguments);
        exitCode.Should().Be(0, "the duplicate-method scanner failed: {0}", error);

        var actual = Entries(output);
        var baseline = SourceTextRatchet.ReadEntries(BaselinePath(relativeBaseline));
        var added = actual.Except(baseline, StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        var stale = baseline.Except(actual, StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();

        string.Join('\n', added).Should().BeEmpty();
        string.Join('\n', stale).Should().BeEmpty();
    }

    private static void AssertBaselineDidNotGrow(string relativeBaseline)
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var current = SourceTextRatchet.ReadEntries(BaselinePath(relativeBaseline));
        var prior = SourceTextRatchet.ReadParentEntries(root, relativeBaseline);
        if (prior is null)
        {
            return;
        }

        var added = current.Select(CanonicalPair)
            .Except(prior.Select(CanonicalPair), StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
        string.Join('\n', added).Should().BeEmpty();
    }

    internal static string CanonicalPair(string pair)
        => string.Join('|', pair.Split('|').Order(StringComparer.Ordinal));

    private static string BaselinePath(string relativeBaseline)
        => SourceTextRatchet.AbsolutePath(ArchitectureTestHelpers.ResolveRepositoryRoot(), relativeBaseline);

    private static string[] Entries(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var script = ArchitectureTestHelpers.CombinePath(root, "scripts", "ci", "duplicate-method-ratchet.py");
        var start = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start python3 {RelativeScript}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("duplicate-method ratchet exceeded 10 minutes.");
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }
}
