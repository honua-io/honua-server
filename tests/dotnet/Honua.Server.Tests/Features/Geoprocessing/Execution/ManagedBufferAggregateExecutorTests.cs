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
/// In-memory unit coverage for the managed (NetTopologySuite)
/// buffer-aggregate executor — the job-dispatchable counterpart to trunk's
/// PostGIS-protocol <c>analytics.buffer-aggregate</c>. Buffers every input
/// feature by 'distance' in the supplied unit and optionally dissolves the
/// result into one feature per groupByFields group with a COUNT attribute.
/// </summary>
public sealed class ManagedBufferAggregateExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task Dissolve_GroupsBuffersByField_AndCarriesCount()
    {
        var executor = new ManagedBufferAggregateExecutor(Options());

        // Two features in group "A" (overlap after buffering) and one in group "B".
        var input = BuildUri(
            Feature(Point(0, 0), ("kind", "A")),
            Feature(Point(0.5, 0), ("kind", "A")),
            Feature(Point(100, 100), ("kind", "B")));

        var (status, uri) = await RunAsync(
            executor,
            ManagedBufferAggregateExecutor.HandledProcessId,
            ("input", input),
            ("distance", "1"),
            ("unit", "meters"),
            ("dissolve", "true"),
            ("groupByFields", "kind"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "one feature per distinct 'kind' group");

        var groupA = features.Single(f => Equals(f.Attributes.GetOptionalValue("kind"), "A"));
        Convert.ToInt64(groupA.Attributes.GetOptionalValue(ManagedBufferAggregateExecutor.CountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(2);

        var groupB = features.Single(f => Equals(f.Attributes.GetOptionalValue("kind"), "B"));
        Convert.ToInt64(groupB.Attributes.GetOptionalValue(ManagedBufferAggregateExecutor.CountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(1);

        // Both group buffers should be valid polygons after the union.
        groupA.Geometry.Should().BeAssignableTo<Polygon>();
        groupB.Geometry.Should().BeAssignableTo<Polygon>();
    }

    [UnitTest]
    public async Task Dissolve_WithoutGroupFields_CollapsesAllInputsIntoOneFeature()
    {
        var executor = new ManagedBufferAggregateExecutor(Options());

        var input = BuildUri(
            Feature(Point(0, 0)),
            Feature(Point(10, 10)),
            Feature(Point(20, 20)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedBufferAggregateExecutor.HandledProcessId,
            ("input", input),
            ("distance", "0.5"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1, "without groupByFields all inputs dissolve into one feature");
        Convert.ToInt64(features[0].Attributes.GetOptionalValue(ManagedBufferAggregateExecutor.CountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(3);
    }

    [UnitTest]
    public async Task DissolveFalse_EmitsOneBufferedFeaturePerInput()
    {
        var executor = new ManagedBufferAggregateExecutor(Options());

        var input = BuildUri(
            Feature(Point(0, 0), ("kind", "A")),
            Feature(Point(100, 100), ("kind", "B")));

        var (status, uri) = await RunAsync(
            executor,
            ManagedBufferAggregateExecutor.HandledProcessId,
            ("input", input),
            ("distance", "1"),
            ("dissolve", "false"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "per-feature buffers when dissolve=false");
        features.Should().AllSatisfy(f =>
            Convert.ToInt64(f.Attributes.GetOptionalValue(ManagedBufferAggregateExecutor.CountAttribute), CultureInfo.InvariantCulture)
                .Should().Be(1));
    }

    [UnitTest]
    public async Task Unit_KilometersIsScaledToCrsUnits()
    {
        var executor = new ManagedBufferAggregateExecutor(Options());

        var input = BuildUri(Feature(Point(0, 0)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedBufferAggregateExecutor.HandledProcessId,
            ("input", input),
            ("distance", "1"),
            ("unit", "kilometers"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);
        var polygon = (Polygon)features[0].Geometry;
        // 1 km buffer ≈ 1000 CRS-unit envelope half-extent.
        polygon.EnvelopeInternal.Width.Should().BeApproximately(2000, 10);
    }

    [UnitTest]
    public async Task MissingDistance_FailsCleanly()
    {
        var executor = new ManagedBufferAggregateExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedBufferAggregateExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(0, 0)))));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task UnsupportedUnit_FailsCleanly()
    {
        var executor = new ManagedBufferAggregateExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedBufferAggregateExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(0, 0)))),
            ("distance", "1"),
            ("unit", "leagues"));

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
        ManagedBufferAggregateExecutor executor,
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
