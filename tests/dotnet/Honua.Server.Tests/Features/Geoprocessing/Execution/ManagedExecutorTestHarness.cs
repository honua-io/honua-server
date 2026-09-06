// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Shared in-memory harness for the managed (NetTopologySuite) vector overlay/
/// proximity/statistics tool packs (#2139, #2140, #2206). Builds inline
/// FeatureCollection data URIs, runs an executor through the canonical
/// step-parameter envelope, and decodes the published FeatureCollection artifact —
/// no Docker, no Postgres, no native dependency.
/// </summary>
internal static class ManagedExecutorTestHarness
{
    public const string DataUriPrefix = "data:application/geo+json;base64,";

    public static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
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

    public static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

    public static Polygon Box(double minX, double minY, double maxX, double maxY)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        });
    }

    public static Feature Feature(Geometry? geometry, params (string Name, object Value)[] attributes)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in attributes)
        {
            table.Add(name, value);
        }

        return new Feature(geometry, table);
    }

    public static string Uri(params IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        return new GeoJsonReader().Read<FeatureCollection>(json).ToList();
    }

    public static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        IProcessExecutor executor,
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
