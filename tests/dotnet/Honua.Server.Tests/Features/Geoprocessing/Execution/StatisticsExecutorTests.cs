// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using static Honua.Server.Tests.Features.Geoprocessing.Execution.ManagedExecutorTestHarness;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the statistics/summarization tool pack (#2140):
/// Summarize-by-group, Frequency, and CalculateStatistics. Table outputs are
/// null-geometry FeatureCollections; no Docker.
/// </summary>
public sealed class StatisticsExecutorTests
{
    private static double D(IFeature f, string key) => Convert.ToDouble(f.Attributes.GetOptionalValue(key), CultureInfo.InvariantCulture);

    private static long L(IFeature f, string key) => Convert.ToInt64(f.Attributes.GetOptionalValue(key), CultureInfo.InvariantCulture);

    [UnitTest]
    public async Task Summarize_SingleCaseField_AggregatesPerGroup()
    {
        var input = Uri(
            Feature(Point(0, 0), ("region", "north"), ("pop", 10)),
            Feature(Point(1, 1), ("region", "north"), ("pop", 30)),
            Feature(Point(2, 2), ("region", "south"), ("pop", 5)));

        var (status, uri) = await RunAsync(
            new StatisticsSummarizeExecutor(Options()),
            StatisticsSummarizeExecutor.HandledProcessId,
            ("input", input),
            ("caseFields", "region"),
            ("statistics", "pop:sum;pop:mean;pop:min;pop:max"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var rows = ReadFeatures(uri!);
        rows.Should().HaveCount(2);

        var north = rows.Single(r => Equals(r.Attributes.GetOptionalValue("region"), "north"));
        L(north, "FREQUENCY").Should().Be(2);
        D(north, "SUM_pop").Should().BeApproximately(40, 1e-6);
        D(north, "MEAN_pop").Should().BeApproximately(20, 1e-6);
        D(north, "MIN_pop").Should().BeApproximately(10, 1e-6);
        D(north, "MAX_pop").Should().BeApproximately(30, 1e-6);

        var south = rows.Single(r => Equals(r.Attributes.GetOptionalValue("region"), "south"));
        L(south, "FREQUENCY").Should().Be(1);
        D(south, "SUM_pop").Should().BeApproximately(5, 1e-6);
    }

    [UnitTest]
    public async Task Summarize_MultipleCaseFields_GroupsByCombination()
    {
        var input = Uri(
            Feature(Point(0, 0), ("region", "n"), ("type", "a"), ("v", 1)),
            Feature(Point(1, 1), ("region", "n"), ("type", "a"), ("v", 3)),
            Feature(Point(2, 2), ("region", "n"), ("type", "b"), ("v", 7)));

        var (status, uri) = await RunAsync(
            new StatisticsSummarizeExecutor(Options()),
            StatisticsSummarizeExecutor.HandledProcessId,
            ("input", input),
            ("caseFields", "region,type"),
            ("statistics", "v:sum"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var rows = ReadFeatures(uri!);
        rows.Should().HaveCount(2, "(n,a) and (n,b) are distinct combinations");
        var na = rows.Single(r => Equals(r.Attributes.GetOptionalValue("type"), "a"));
        D(na, "SUM_v").Should().BeApproximately(4, 1e-6);
    }

    [UnitTest]
    public async Task Summarize_NullCaseValueFormsOwnGroup_AndNullsSkippedInAggregate()
    {
        var input = Uri(
            Feature(Point(0, 0), ("region", "north"), ("pop", 10)),
            // Null pop is skipped from SUM; this row still counts toward FREQUENCY.
            Feature(Point(1, 1), ("region", "north")),
            // Missing region -> its own (null) group.
            Feature(Point(2, 2), ("pop", 99)));

        var (status, uri) = await RunAsync(
            new StatisticsSummarizeExecutor(Options()),
            StatisticsSummarizeExecutor.HandledProcessId,
            ("input", input),
            ("caseFields", "region"),
            ("statistics", "pop:sum"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var rows = ReadFeatures(uri!);
        rows.Should().HaveCount(2);

        var north = rows.Single(r => Equals(r.Attributes.GetOptionalValue("region"), "north"));
        L(north, "FREQUENCY").Should().Be(2, "both north rows count even though one has a null pop");
        D(north, "SUM_pop").Should().BeApproximately(10, 1e-6, "the null pop is excluded from the sum");

        var nullGroup = rows.Single(r => r.Attributes.GetOptionalValue("region") == null);
        L(nullGroup, "FREQUENCY").Should().Be(1);
    }

    [UnitTest]
    public async Task Frequency_CountsDistinctCombinations()
    {
        var input = Uri(
            Feature(Point(0, 0), ("a", "x"), ("b", "1")),
            Feature(Point(1, 1), ("a", "x"), ("b", "1")),
            Feature(Point(2, 2), ("a", "x"), ("b", "2")));

        var (status, uri) = await RunAsync(
            new StatisticsFrequencyExecutor(Options()),
            StatisticsFrequencyExecutor.HandledProcessId,
            ("input", input),
            ("frequencyFields", "a,b"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var rows = ReadFeatures(uri!);
        rows.Should().HaveCount(2);
        var x1 = rows.Single(r => Equals(r.Attributes.GetOptionalValue("b"), "1"));
        L(x1, "FREQUENCY").Should().Be(2);
    }

    [UnitTest]
    public async Task Frequency_SummaryFieldsSummedPerCombination()
    {
        var input = Uri(
            Feature(Point(0, 0), ("cls", "k"), ("amt", 10)),
            Feature(Point(1, 1), ("cls", "k"), ("amt", 15)));

        var (status, uri) = await RunAsync(
            new StatisticsFrequencyExecutor(Options()),
            StatisticsFrequencyExecutor.HandledProcessId,
            ("input", input),
            ("frequencyFields", "cls"),
            ("summaryFields", "amt"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var rows = ReadFeatures(uri!);
        rows.Should().ContainSingle();
        L(rows[0], "FREQUENCY").Should().Be(2);
        D(rows[0], "SUM_amt").Should().BeApproximately(25, 1e-6);
    }

    [UnitTest]
    public async Task Calculate_ComputesDescriptiveStatisticsPerField()
    {
        var input = Uri(
            Feature(Point(0, 0), ("v", 2)),
            Feature(Point(1, 1), ("v", 4)),
            Feature(Point(2, 2), ("v", 4)));

        var (status, uri) = await RunAsync(
            new StatisticsCalculateExecutor(Options()),
            StatisticsCalculateExecutor.HandledProcessId,
            ("input", input),
            ("fields", "v"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var rows = ReadFeatures(uri!);
        rows.Should().ContainSingle();
        var row = rows[0];
        row.Attributes.GetOptionalValue("FIELD").Should().Be("v");
        L(row, "COUNT").Should().Be(3);
        D(row, "MIN").Should().BeApproximately(2, 1e-6);
        D(row, "MAX").Should().BeApproximately(4, 1e-6);
        D(row, "MEAN").Should().BeApproximately(10.0 / 3.0, 1e-6);
        D(row, "SUM").Should().BeApproximately(10, 1e-6);
        // Sample stddev of {2,4,4}: mean 10/3, variance = ((2-10/3)^2+2*(4-10/3)^2)/2.
        D(row, "STDDEV").Should().BeApproximately(1.1547005383792515, 1e-9);
    }

    [UnitTest]
    public async Task Calculate_StdDevNullForFewerThanTwoValues()
    {
        var input = Uri(Feature(Point(0, 0), ("v", 5)));

        var (status, uri) = await RunAsync(
            new StatisticsCalculateExecutor(Options()),
            StatisticsCalculateExecutor.HandledProcessId,
            ("input", input),
            ("fields", "v"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!)[0].Attributes.GetOptionalValue("STDDEV").Should().BeNull();
    }

    [UnitTest]
    public async Task Frequency_MissingFrequencyFields_FailsCleanly()
    {
        var (status, _) = await RunAsync(
            new StatisticsFrequencyExecutor(Options()),
            StatisticsFrequencyExecutor.HandledProcessId,
            ("input", Uri(Feature(Point(0, 0), ("a", "x")))));

        status.Should().Be(ExecutionJobStatus.Failed);
    }
}
