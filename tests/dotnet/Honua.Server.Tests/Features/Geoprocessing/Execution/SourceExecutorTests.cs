// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the reconciled GeoETL source executors
/// (source.geojson, source.csv). Each parses an inline document and publishes a
/// FeatureCollection artifact. Pure managed parsing — no native dependency, no Docker.
/// </summary>
public sealed class SourceExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task GeoJsonSource_InlineDocument_ParsesFeatures()
    {
        var executor = new GeoJsonSourceExecutor(Options());
        const string inline = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"a"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{"name":"b"}}
        ]}
        """;

        var (status, uri) = await RunAsync(
            executor, GeoJsonSourceExecutor.HandledProcessId, ("inline", inline));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
        features[0].Attributes.GetOptionalValue("name").Should().Be("a");
    }

    [UnitTest]
    public async Task GeoJsonSource_FromDataUri_RoundTrips()
    {
        var executor = new GeoJsonSourceExecutor(Options());
        var collection = new FeatureCollection
        {
            new Feature(
                NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(7, 8)),
                new AttributesTable { { "k", "v" } })
        };
        var json = new GeoJsonWriter().Write(collection);
        var dataUri = DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var (status, uri) = await RunAsync(
            executor, GeoJsonSourceExecutor.HandledProcessId, ("input", dataUri));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);
        features[0].Attributes.GetOptionalValue("k").Should().Be("v");
    }

    [UnitTest]
    public async Task GeoJsonSource_NoInput_FailsCleanly()
    {
        var executor = new GeoJsonSourceExecutor(Options());
        var (status, _) = await RunAsync(executor, GeoJsonSourceExecutor.HandledProcessId);
        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task CsvSource_LonLatColumns_DerivesPointGeometry()
    {
        var executor = new CsvSourceExecutor(Options());
        const string inline = "name,lon,lat\nalpha,10,20\nbeta,30,40\n";

        var (status, uri) = await RunAsync(
            executor, CsvSourceExecutor.HandledProcessId, ("inline", inline));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
        var point = features[0].Geometry as Point;
        point!.X.Should().BeApproximately(10, 1e-9);
        point.Y.Should().BeApproximately(20, 1e-9);
        features[0].Attributes.GetOptionalValue("name").Should().Be("alpha");
    }

    [UnitTest]
    public async Task CsvSource_WktColumn_ParsesGeometry()
    {
        var executor = new CsvSourceExecutor(Options());
        const string inline = "id,wkt\n1,\"POINT (5 6)\"\n";

        var (status, uri) = await RunAsync(
            executor, CsvSourceExecutor.HandledProcessId, ("inline", inline));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);
        var point = features[0].Geometry as Point;
        point!.X.Should().BeApproximately(5, 1e-9);
        features[0].Attributes.GetOptionalValue("id").Should().Be("1");
    }

    [UnitTest]
    public async Task CsvSource_NoInline_FailsCleanly()
    {
        var executor = new CsvSourceExecutor(Options());
        var (status, _) = await RunAsync(executor, CsvSourceExecutor.HandledProcessId);
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

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        return new GeoJsonReader().Read<FeatureCollection>(json).ToList();
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
