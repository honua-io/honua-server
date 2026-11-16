// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// The baseline of src/ files whose types only tests use may only shrink.
[Trait("Category", "Architecture")]
public sealed class TestOnlyProductionRatchetTests
{
    private const string RelativeBaseline = "tests/dotnet/Honua.Architecture.Tests/test-only-production-baseline.txt";

    [ArchitectureTest]
    public async Task Scanner_SelfTest_Passes()
    {
        var (exitCode, _, error) = await RunScannerAsync("--self-test");
        exitCode.Should().Be(0, "the test-only production scanner self-test failed: {0}", error);
    }

    [ArchitectureTest]
    public async Task TestOnlyProductionFiles_MustMatchTheBaseline()
    {
        var (exitCode, output, error) = await RunScannerAsync();
        exitCode.Should().Be(0, "the test-only production scanner failed: {0}", error);

        var actual = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var baseline = SourceTextRatchet.ReadEntries(BaselinePath());
        string.Join('\n', actual.Except(baseline, StringComparer.Ordinal).Order(StringComparer.Ordinal)).Should().BeEmpty(
            "production code used only by tests must be deleted with its tests, not added");
        string.Join('\n', baseline.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal)).Should().BeEmpty(
            "delete baseline lines the scanner no longer reports");
    }

    [ArchitectureTest]
    public void TestOnlyProductionBaseline_MustNotGrowFromTheCheckedOutBase()
    {
        var prior = SourceTextRatchet.ReadParentEntries(ArchitectureTestHelpers.ResolveRepositoryRoot(), RelativeBaseline);
        if (prior is null)
        {
            return;
        }

        var current = SourceTextRatchet.ReadEntries(BaselinePath());
        string.Join('\n', current.Except(prior, StringComparer.Ordinal).Order(StringComparer.Ordinal)).Should().BeEmpty();
    }

    private static string BaselinePath()
        => SourceTextRatchet.AbsolutePath(ArchitectureTestHelpers.ResolveRepositoryRoot(), RelativeBaseline);

    private static async Task<(int ExitCode, string Output, string Error)> RunScannerAsync(params string[] arguments)
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var start = new ProcessStartInfo("python3") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(ArchitectureTestHelpers.CombinePath(root, "scripts", "ci", "test-only-production-ratchet.py"));
        arguments.ToList().ForEach(start.ArgumentList.Add);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start python3.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return (process.ExitCode, await output, await error);
    }
}
