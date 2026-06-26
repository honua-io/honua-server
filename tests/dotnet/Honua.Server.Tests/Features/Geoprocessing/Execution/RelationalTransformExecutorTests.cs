// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the relational GeoETL transform executors added
/// alongside the safe expression engine: transform.computed-field (op=expression),
/// transform.attribute-join (inner/left hash join), transform.aggregate (group-by
/// with scalar + geometry aggregates), and transform.pivot / transform.unpivot.
/// Each builds an input FeatureCollection, runs the executor, and asserts the
/// published FeatureCollection data URI carries the expected attributes/geometry.
/// </summary>
public sealed class RelationalTransformExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    private static readonly string[] ExpectedMeltedColumns = { "pop", "area" };

    // ----- transform.computed-field (expression mode) ------------------------

    [UnitTest]
    public async Task ComputedField_Expression_EvaluatesStringAndCast()
    {
        var executor = new ComputedFieldTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(0, 0), ("name", "  Acme  "), ("year", 2026)));

        var (status, uri) = await RunAsync(
            executor,
            ComputedFieldTransformExecutor.HandledProcessId,
            ("input", input),
            ("target", "label"),
            ("op", "expression"),
            ("expression", "upper(trim(name)) + \"-\" + cast(year, string)"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features[0].Attributes.GetOptionalValue("label").Should().Be("ACME-2026");
    }

    [UnitTest]
    public async Task ComputedField_Expression_InferredWhenOpOmitted()
    {
        var executor = new ComputedFieldTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(0, 0), ("a", 10), ("b", 5)));

        var (status, uri) = await RunAsync(
            executor,
            ComputedFieldTransformExecutor.HandledProcessId,
            ("input", input),
            ("target", "sum"),
            ("expression", "a + b"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        Convert.ToDouble(ReadFeatures(uri!)[0].Attributes.GetOptionalValue("sum"), CultureInfo.InvariantCulture)
            .Should().BeApproximately(15d, 1e-9);
    }

    [UnitTest]
    public async Task ComputedField_Expression_InvalidSyntax_FailsCleanly()
    {
        var executor = new ComputedFieldTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(0, 0), ("a", 1)));

        var (status, _) = await RunAsync(
            executor,
            ComputedFieldTransformExecutor.HandledProcessId,
            ("input", input),
            ("target", "x"),
            ("op", "expression"),
            ("expression", "a + + "));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task ComputedField_LegacyAddOp_StillWorks()
    {
        var executor = new ComputedFieldTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(0, 0), ("a", 3), ("b", 4)));

        var (status, uri) = await RunAsync(
            executor,
            ComputedFieldTransformExecutor.HandledProcessId,
            ("input", input),
            ("target", "sum"),
            ("op", "add"),
            ("left", "a"),
            ("right", "b"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        Convert.ToDouble(ReadFeatures(uri!)[0].Attributes.GetOptionalValue("sum"), CultureInfo.InvariantCulture)
            .Should().BeApproximately(7d, 1e-9);
    }

    // ----- transform.attribute-join ------------------------------------------

    [UnitTest]
    public async Task AttributeJoin_Inner_BringsRightFields_DropsUnmatched()
    {
        var executor = new AttributeJoinTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("id", "a"), ("name", "alpha")),
            Feature(Point(1, 1), ("id", "z"), ("name", "zeta")));
        var right = BuildInputUri(
            Feature(Point(0, 0), ("id", "a"), ("pop", 100)));

        var (status, uri) = await RunAsync(
            executor,
            AttributeJoinTransformExecutor.HandledProcessId,
            ("input", input),
            ("right", right),
            ("leftKeys", "id"),
            ("rightKeys", "id"),
            ("fields", "pop"),
            ("type", "inner"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1, "only the matching input row survives an inner join");
        features[0].Attributes.GetOptionalValue("name").Should().Be("alpha");
        Convert.ToDouble(features[0].Attributes.GetOptionalValue("pop"), CultureInfo.InvariantCulture).Should().Be(100d);
    }

    [UnitTest]
    public async Task AttributeJoin_Left_PreservesUnmatchedWithNullCarriedField()
    {
        var executor = new AttributeJoinTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("id", "a")),
            Feature(Point(1, 1), ("id", "z")));
        var right = BuildInputUri(Feature(Point(0, 0), ("id", "a"), ("pop", 100)));

        var (status, uri) = await RunAsync(
            executor,
            AttributeJoinTransformExecutor.HandledProcessId,
            ("input", input),
            ("right", right),
            ("leftKeys", "id"),
            ("fields", "pop"),
            ("type", "left"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "left join keeps every input feature");
        var unmatched = features.Single(f => Equals(f.Attributes.GetOptionalValue("id"), "z"));
        unmatched.Attributes.Exists("pop").Should().BeTrue();
        unmatched.Attributes.GetOptionalValue("pop").Should().BeNull();
    }

    // ----- transform.aggregate -----------------------------------------------

    [UnitTest]
    public async Task Aggregate_SumAndMeanPerGroup()
    {
        var executor = new AggregateTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("region", "n"), ("pop", 10)),
            Feature(Point(1, 1), ("region", "n"), ("pop", 30)),
            Feature(Point(2, 2), ("region", "s"), ("pop", 5)));

        var (status, uri) = await RunAsync(
            executor,
            AggregateTransformExecutor.HandledProcessId,
            ("input", input),
            ("groupBy", "region"),
            ("aggregates", "pop:sum:total;pop:mean:avg"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
        var north = features.Single(f => Equals(f.Attributes.GetOptionalValue("region"), "n"));
        Convert.ToDouble(north.Attributes.GetOptionalValue("total"), CultureInfo.InvariantCulture).Should().Be(40d);
        Convert.ToDouble(north.Attributes.GetOptionalValue("avg"), CultureInfo.InvariantCulture).Should().Be(20d);
    }

    [UnitTest]
    public async Task Aggregate_Collect_JoinsMemberValues()
    {
        var executor = new AggregateTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("region", "n"), ("name", "a")),
            Feature(Point(1, 1), ("region", "n"), ("name", "b")));

        var (status, uri) = await RunAsync(
            executor,
            AggregateTransformExecutor.HandledProcessId,
            ("input", input),
            ("groupBy", "region"),
            ("aggregates", "name:collect:names"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        Convert.ToString(features[0].Attributes.GetOptionalValue("names"), CultureInfo.InvariantCulture)
            .Should().Be("a,b");
    }

    [UnitTest]
    public async Task Aggregate_GeometryUnion_PerGroup_ProducesNonEmptyGeometry()
    {
        var executor = new AggregateTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Square(0, 0, 1), ("region", "n")),
            Feature(Square(0.5, 0, 1), ("region", "n")));

        var (status, uri) = await RunAsync(
            executor,
            AggregateTransformExecutor.HandledProcessId,
            ("input", input),
            ("groupBy", "region"),
            ("aggregates", "region:count"),
            ("geometry", "union"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);
        features[0].Geometry.Should().NotBeNull();
        features[0].Geometry!.IsEmpty.Should().BeFalse();
        // Two overlapping unit squares union to less than their summed area (2.0).
        features[0].Geometry!.Area.Should().BeLessThan(2.0).And.BeGreaterThan(1.0);
    }

    // ----- transform.pivot / transform.unpivot -------------------------------

    [UnitTest]
    public async Task Pivot_LongToWide_SpreadsLabelColumnIntoColumns()
    {
        var executor = new PivotTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("city", "ny"), ("metric", "pop"), ("v", 8)),
            Feature(Point(0, 0), ("city", "ny"), ("metric", "area"), ("v", 300)),
            Feature(Point(1, 1), ("city", "la"), ("metric", "pop"), ("v", 4)));

        var (status, uri) = await RunAsync(
            executor,
            PivotTransformExecutor.HandledProcessId,
            ("input", input),
            ("groupBy", "city"),
            ("pivotField", "metric"),
            ("valueField", "v"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
        var ny = features.Single(f => Equals(f.Attributes.GetOptionalValue("city"), "ny"));
        Convert.ToDouble(ny.Attributes.GetOptionalValue("pop"), CultureInfo.InvariantCulture).Should().Be(8d);
        Convert.ToDouble(ny.Attributes.GetOptionalValue("area"), CultureInfo.InvariantCulture).Should().Be(300d);
    }

    [UnitTest]
    public async Task Unpivot_WideToLong_MeltsValueColumns()
    {
        var executor = new UnpivotTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("city", "ny"), ("pop", 8), ("area", 300)));

        var (status, uri) = await RunAsync(
            executor,
            UnpivotTransformExecutor.HandledProcessId,
            ("input", input),
            ("fields", "pop,area"),
            ("keep", "city"),
            ("nameField", "metric"),
            ("valueField", "value"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "one row per melted column");
        features.Should().OnlyContain(f => Equals(f.Attributes.GetOptionalValue("city"), "ny"));
        features.Select(f => Convert.ToString(f.Attributes.GetOptionalValue("metric"), CultureInfo.InvariantCulture))
            .Should().BeEquivalentTo(ExpectedMeltedColumns);
    }

    // -------------------------------------------------------------------------
    // Helpers (mirror AttributeTransformExecutorTests)
    // -------------------------------------------------------------------------

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

    private static Polygon Square(double x, double y, double size)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        var ring = factory.CreateLinearRing(new[]
        {
            new Coordinate(x, y),
            new Coordinate(x + size, y),
            new Coordinate(x + size, y + size),
            new Coordinate(x, y + size),
            new Coordinate(x, y),
        });
        return factory.CreatePolygon(ring);
    }

    private static Feature Feature(Geometry geometry, params (string Name, object Value)[] attributes)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in attributes)
        {
            table.Add(name, value);
        }

        return new Feature(geometry, table);
    }

    private static string BuildInputUri(params IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        var collection = new GeoJsonReader().Read<FeatureCollection>(json);
        return collection.ToList();
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        IJobExecutor executor,
        string processId,
        params (string Name, string Value)[] inputs)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");
        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        foreach (var (name, value) in inputs)
        {
            parameters[prefix + name] = value;
        }

        var record = new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri);
    }
}
