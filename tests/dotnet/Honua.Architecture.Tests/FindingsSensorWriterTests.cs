// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards against the "dead sensor" class of bug (honua-server#2805): an ops-findings rule that
/// reads a metric whose only writers are test code, so the rule can never fire in production and
/// the health surface silently lies during the very incident it exists to detect.
///
/// Each entry in <see cref="FindingsRuleSensors"/> names a metric-mutator method that a findings
/// rule ultimately depends on. The test asserts every such mutator has at least one production
/// call site (in <c>src/</c>, not just tests). When a new findings-rule sensor is added, register
/// its writer here; if it has no production writer the build fails, exactly as it should have when
/// the connection-pool pressure counters were wired to dead code.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class FindingsSensorWriterTests
{
    /// <summary>
    /// (sensor type, mutator method, findings rule it feeds). The mutator is the production writer
    /// of the metric the rule compares against a threshold.
    /// </summary>
    private static readonly (string TypeName, string MutatorMethod, string FeedsRule)[] FindingsRuleSensors =
    {
        // The db-bounded-admission-pressure rule reads the admission gate's windowed
        // acquisition-timeout signal; the gate records it from the real WaitAsync timeout path.
        ("QueryConcurrencyGate", "RecordAcquisitionTimeout", "db-bounded-admission-pressure"),
    };

    [ArchitectureTest]
    public void EveryFindingsRuleSensorMutator_HasAProductionWriter()
    {
        var productionSource = EnumerateProductionSourceFiles().ToList();
        productionSource.Should().NotBeEmpty("the production source tree must be discoverable for this guard to be meaningful");

        var text = productionSource.Select(File.ReadAllText).ToList();

        foreach (var sensor in FindingsRuleSensors)
        {
            var token = sensor.MutatorMethod + "(";
            var totalOccurrences = text.Sum(source => CountOccurrences(source, token));

            // A live sensor appears at least twice in production source: once as the method
            // declaration and at least once as a call site. A count of 1 means the mutator is
            // declared but never invoked outside tests — the honua-server#2805 dead-counter smell.
            totalOccurrences.Should().BeGreaterThan(
                1,
                $"the '{sensor.FeedsRule}' findings rule depends on {sensor.TypeName}.{sensor.MutatorMethod}, "
                + "which must be written by production code (not only tests) or the rule can never fire");
        }
    }

    private static IEnumerable<string> EnumerateProductionSourceFiles()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var srcRoot = Path.Combine(repositoryRoot, "src");
        if (!Directory.Exists(srcRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !PathContainsSegment(path, "obj") && !PathContainsSegment(path, "bin"));
    }

    private static bool PathContainsSegment(string path, string segment)
        => path.Contains($"{Path.DirectorySeparatorChar}{segment}{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.Contains($"{Path.AltDirectorySeparatorChar}{segment}{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private static int CountOccurrences(string source, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
