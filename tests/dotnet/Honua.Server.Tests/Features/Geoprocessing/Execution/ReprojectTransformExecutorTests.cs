// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
// IJobExecutionContext is in the Abstractions namespace above.
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
/// In-memory unit coverage for transform.reproject, reusing the trunk
/// CoordinateTransformer (no native dependency). Verifies the WGS 84 → Web
/// Mercator case, identity passthrough, attribute carry-through, and rejection
/// of unsupported datum-shift pairs.
/// </summary>
public sealed class ReprojectTransformExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task Reproject_Wgs84ToWebMercator_TransformsCoordinatesAndKeepsAttributes()
    {
        var executor = new ReprojectTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(0, 0), ("id", 1)));

        var (status, uri) = await RunAsync(
            executor,
            ReprojectTransformExecutor.HandledProcessId,
            ("input", input),
            ("fromSrid", "4326"),
            ("toSrid", "3857"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);
        // Origin maps to (0,0) in Web Mercator as well, so test a non-origin point too.
        features[0].Attributes.GetOptionalValue("id").Should().Be(1L);
    }

    [UnitTest]
    public async Task Reproject_Wgs84ToWebMercator_NonOrigin_ScalesX()
    {
        var executor = new ReprojectTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(45, 0)));

        var (status, uri) = await RunAsync(
            executor,
            ReprojectTransformExecutor.HandledProcessId,
            ("input", input),
            ("fromSrid", "4326"),
            ("toSrid", "3857"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var geom = ReadFeatures(uri!)[0].Geometry as Point;
        geom!.X.Should().BeApproximately(5009377.085, 1.0, "longitude 45 maps to ~5,009,377 m in Web Mercator");
    }

    [UnitTest]
    public async Task Reproject_Identity_PassesThrough()
    {
        var executor = new ReprojectTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(10, 20)));

        var (status, uri) = await RunAsync(
            executor,
            ReprojectTransformExecutor.HandledProcessId,
            ("input", input),
            ("fromSrid", "4326"),
            ("toSrid", "4326"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var geom = ReadFeatures(uri!)[0].Geometry as Point;
        geom!.X.Should().BeApproximately(10, 1e-9);
        geom.Y.Should().BeApproximately(20, 1e-9);
    }

    [UnitTest]
    public async Task Reproject_3DPoint_Wgs84ToWebMercator_PreservesZ()
    {
        var executor = new ReprojectTransformExecutor(Options());
        var input = BuildInputUri(Feature(PointZ(45, 0, 123.5)));

        var (status, uri) = await RunAsync(
            executor,
            ReprojectTransformExecutor.HandledProcessId,
            ("input", input),
            ("fromSrid", "4326"),
            ("toSrid", "3857"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var geom = ReadFeatures(uri!)[0].Geometry as Point;
        geom!.X.Should().BeApproximately(5009377.085, 1.0);
        double.IsFinite(geom.Coordinates[0].Z).Should().BeTrue("Z must survive reprojection (#2744)");
        geom.Coordinates[0].Z.Should().BeApproximately(123.5, 1e-9);
    }

    [UnitTest]
    public async Task Reproject_3DPoint_IdentityPassthrough_PreservesZ()
    {
        var executor = new ReprojectTransformExecutor(Options());
        var input = BuildInputUri(Feature(PointZ(10, 20, 55.25)));

        var (status, uri) = await RunAsync(
            executor,
            ReprojectTransformExecutor.HandledProcessId,
            ("input", input),
            ("fromSrid", "4326"),
            ("toSrid", "4326"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var geom = ReadFeatures(uri!)[0].Geometry as Point;
        geom!.Coordinates[0].Z.Should().BeApproximately(55.25, 1e-9,
            "identity passthrough and the transformed path must both preserve Z");
    }

    [UnitTest]
    public async Task Reproject_EmptyGeometry_IsStampedWithTargetSrid()
    {
        var executor = new ReprojectTransformExecutor(Options());

        // Force the spill (NDJSON) output path: it is the only artifact shape that
        // carries per-feature SRID (__honua_srid), so the toSrid stamp on empty
        // geometries is observable. A ~27k-point line crosses the publisher's
        // 1 MiB inline threshold (~40 estimated bytes per coordinate).
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        var emptyPoint = factory.CreatePoint();
        var bigLine = factory.CreateLineString(
            Enumerable.Range(0, 27_000).Select(i => new Coordinate(i * 0.001, i * 0.001)).ToArray());
        var input = BuildInputUri(Feature(emptyPoint), Feature(bigLine));

        var (status, uri) = await RunAsync(
            executor,
            ReprojectTransformExecutor.HandledProcessId,
            ("input", input),
            ("fromSrid", "4326"),
            ("toSrid", "3857"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        FeatureStreamArtifact.IsStreamReference(uri).Should().BeTrue("the oversized output must spill to NDJSON");
        FeatureStreamArtifact.TryOpenRead(uri, out _, out var stream).Should().BeTrue();

        var features = new List<IFeature>();
        await foreach (var feature in stream)
        {
            features.Add(feature);
        }

        features.Should().HaveCount(2);
        var empty = features.Single(f => f.Geometry!.IsEmpty);
        empty.Geometry!.SRID.Should().Be(3857,
            "empty geometries move to the target CRS like non-empty ones instead of leaking the source SRID (#2744)");
        features.Single(f => !f.Geometry!.IsEmpty).Geometry!.SRID.Should().Be(3857);
    }

    [UnitTest]
    public async Task Reproject_UnsupportedDatumShift_FailsCleanly()
    {
        var executor = new ReprojectTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(0, 0)));

        var (status, _) = await RunAsync(
            executor,
            ReprojectTransformExecutor.HandledProcessId,
            ("input", input),
            ("fromSrid", "4326"),
            ("toSrid", "2193")); // NZTM — requires PROJ/ST_Transform

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

    private static Point PointZ(double x, double y, double z)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new CoordinateZ(x, y, z));

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

        // Dimension-3 writer so 3D fixtures survive input encoding (2D output is unchanged).
        var json = GeoJsonArtifactCodec.CreateWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        // Dimension-3 reader so Z assertions can observe the third ordinate.
        return GeoJsonArtifactCodec.CreateReader().Read<FeatureCollection>(json).ToList();
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        ReprojectTransformExecutor executor,
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
