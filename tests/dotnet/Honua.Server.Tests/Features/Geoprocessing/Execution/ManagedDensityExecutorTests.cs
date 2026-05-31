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
/// In-memory unit coverage for the managed (NetTopologySuite) density
/// executor — the job-dispatchable counterpart to trunk's PostGIS-protocol
/// <c>analytics.density</c>. Snaps each input geometry's point representative
/// onto a square or hex grid of cellSize CRS units and emits one feature
/// per non-empty cell with COUNT (and optionally SUM_&lt;weightField&gt;).
/// </summary>
public sealed class ManagedDensityExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task SquareMode_BucketsPointsByCellAndEmitsPerCellCount()
    {
        var executor = new ManagedDensityExecutor(Options());

        // Three points fall into cell (0,0) (cellSize=10), one into (1,0), one into (0,1).
        var input = BuildUri(
            Feature(Point(1, 1)),
            Feature(Point(2, 2)),
            Feature(Point(3, 3)),
            Feature(Point(15, 1)),
            Feature(Point(1, 15)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedDensityExecutor.HandledProcessId,
            ("input", input),
            ("mode", "square"),
            ("cellSize", "10"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(3, "three distinct cells received input");

        var counts = features.ToDictionary(
            f => (
                Convert.ToInt64(f.Attributes.GetOptionalValue(ManagedDensityExecutor.CellColumnAttribute), CultureInfo.InvariantCulture),
                Convert.ToInt64(f.Attributes.GetOptionalValue(ManagedDensityExecutor.CellRowAttribute), CultureInfo.InvariantCulture)),
            f => Convert.ToInt64(f.Attributes.GetOptionalValue(ManagedDensityExecutor.CountAttribute), CultureInfo.InvariantCulture));

        counts[(0L, 0L)].Should().Be(3);
        counts[(1L, 0L)].Should().Be(1);
        counts[(0L, 1L)].Should().Be(1);

        features.Should().AllSatisfy(f => f.Geometry.Should().BeAssignableTo<Polygon>());
    }

    [UnitTest]
    public async Task HexMode_BucketsPointsByCell_AndEmitsHexagonGeometry()
    {
        var executor = new ManagedDensityExecutor(Options());

        // Two points near the origin should land in the same hex cell at cellSize=100.
        var input = BuildUri(
            Feature(Point(0, 0)),
            Feature(Point(1, 1)),
            Feature(Point(500, 0)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedDensityExecutor.HandledProcessId,
            ("input", input),
            ("mode", "hex"),
            ("cellSize", "100"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Count.Should().BeGreaterThan(0);

        // Sum of per-cell counts must equal input count.
        var total = features.Sum(f => Convert.ToInt64(
            f.Attributes.GetOptionalValue(ManagedDensityExecutor.CountAttribute),
            CultureInfo.InvariantCulture));
        total.Should().Be(3);

        // Pointy-top hex cells have six exterior vertices (closed ring → 7 coordinates).
        features.Should().AllSatisfy(f =>
        {
            var polygon = (Polygon)f.Geometry;
            polygon.ExteriorRing.Coordinates.Length.Should().Be(7);
        });
    }

    [UnitTest]
    public async Task WeightField_SumsNumericWeights_AndSkipsMissingOrNonNumeric()
    {
        var executor = new ManagedDensityExecutor(Options());

        // All three points fall in the same square cell.
        var input = BuildUri(
            Feature(Point(1, 1), ("pop", 10)),
            Feature(Point(2, 2), ("pop", 5)),
            Feature(Point(3, 3), ("pop", "n/a")));

        var (status, uri) = await RunAsync(
            executor,
            ManagedDensityExecutor.HandledProcessId,
            ("input", input),
            ("mode", "square"),
            ("cellSize", "10"),
            ("weightField", "pop"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);

        Convert.ToInt64(features[0].Attributes.GetOptionalValue(ManagedDensityExecutor.CountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(3);
        Convert.ToDouble(features[0].Attributes.GetOptionalValue("SUM_pop"), CultureInfo.InvariantCulture)
            .Should().BeApproximately(15, 1e-6);
    }

    [UnitTest]
    public async Task OutputCells_PreserveTheInputSrid()
    {
        var executor = new ManagedDensityExecutor(Options());

        // Source factory uses SRID 4326; emitted cell polygons must inherit it.
        var input = BuildUri(
            Feature(Point(0, 0)),
            Feature(Point(20, 20)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedDensityExecutor.HandledProcessId,
            ("input", input),
            ("mode", "square"),
            ("cellSize", "10"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().NotBeEmpty();
        features.Should().AllSatisfy(f => f.Geometry.SRID.Should().Be(4326));
    }

    [UnitTest]
    public async Task MissingCellSize_FailsCleanly()
    {
        var executor = new ManagedDensityExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedDensityExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(0, 0)))));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task UnsupportedMode_FailsCleanly()
    {
        var executor = new ManagedDensityExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedDensityExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(0, 0)))),
            ("mode", "triangle"),
            ("cellSize", "10"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

    private static Feature Feature(Geometry geometry, params (string Name, object Value)[] attributes)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in attributes)
        {
            table.Add(name, value);
        }

        return new Feature(geometry, table);
    }

    private static string BuildUri(params IFeature[] features)
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
        return new GeoJsonReader().Read<FeatureCollection>(json).ToList();
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        ManagedDensityExecutor executor,
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
