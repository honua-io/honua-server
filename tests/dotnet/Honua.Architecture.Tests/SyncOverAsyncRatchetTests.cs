// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ratchet for blocking waits on tasks under <c>src/</c>.
/// </summary>
/// <remarks>
/// Flags <c>GetAwaiter().GetResult()</c>, empty <c>.Wait()</c>, <c>Task.WaitAll</c>,
/// <c>Task.WaitAny</c>, and reading <c>.Result</c> from a <c>*Task</c> or from an
/// <c>*Async(...)</c> call. Property reads such as <c>response.Result</c> are not
/// blocking waits and are left alone. <c>Semaphore.Wait</c> takes arguments, so it
/// does not match empty <c>.Wait()</c>. Comment lines are ignored. The baseline may
/// only shrink once it is on trunk.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed partial class SyncOverAsyncRatchetTests
{
    private const string RelativeBaseline = "tests/dotnet/Honua.Architecture.Tests/sync-over-async-baseline.txt";

    [ArchitectureTest]
    public void BlockingTaskWaits_MustMatchTheBaseline()
    {
        var actual = Scan();
        var baseline = SourceTextRatchet.ReadEntries(BaselinePath());
        var added = actual.Except(baseline, StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        var stale = baseline.Except(actual, StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();

        string.Join('\n', added).Should().BeEmpty();
        string.Join('\n', stale).Should().BeEmpty();
    }

    [ArchitectureTest]
    public void SyncOverAsyncBaseline_MustNotGrowFromTheCheckedOutBase()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var current = SourceTextRatchet.ReadEntries(BaselinePath());
        var prior = SourceTextRatchet.ReadParentEntries(root, RelativeBaseline);
        if (prior is null)
        {
            return;
        }

        var added = current.Except(prior, StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        string.Join('\n', added).Should().BeEmpty();
    }

    private static string BaselinePath()
        => SourceTextRatchet.AbsolutePath(ArchitectureTestHelpers.ResolveRepositoryRoot(), RelativeBaseline);

    internal static string[] Scan()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var src = ArchitectureTestHelpers.CombinePath(root, "src");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                var stripped = line.TrimStart();
                if (stripped.StartsWith("//", StringComparison.Ordinal) ||
                    stripped.StartsWith('*') ||
                    stripped.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                var code = line.Split("//", 2)[0];
                if (BlockingCall().IsMatch(code))
                {
                    var key = relative + "\t" + code.Trim();
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }

        return counts
            .Select(pair => pair.Value == 1 ? pair.Key : pair.Key + "\tx" + pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
    }

    [GeneratedRegex(
        """(?<![A-Za-z0-9_])(?:GetAwaiter\(\)\.GetResult\(\)|\.Wait\(\)|Task\.WaitAll\(|Task\.WaitAny\(|[A-Za-z_][A-Za-z0-9_]*Task\.Result\b|[A-Za-z_][A-Za-z0-9_]*Async\s*\([^;\n]*\)\s*\.Result\b)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex BlockingCall();
}
