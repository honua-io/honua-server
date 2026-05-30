// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage of the slice-2 <c>geometry.project</c> executor.
/// </summary>
public sealed class GeometryProjectJobExecutorTests
{
    private const string PointWkbBase64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA"; // POINT(0 0)

    [UnitTest]
    public async Task ExecuteAsync_IdentityTransform_PublishesUnchangedGeometry()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-identity");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var record = CreateJobRecord(
            GeometryProjectJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: PointWkbBase64,
            fromSrid: 4326,
            toSrid: 4326);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();

        var bytes = Convert.FromBase64String(publishedUri!["data:application/geo+json;base64,".Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("properties").GetProperty("processId").GetString()
            .Should().Be("geometry.project");
        doc.RootElement.GetProperty("properties").GetProperty("fromSrid").GetInt32().Should().Be(4326);
        doc.RootElement.GetProperty("properties").GetProperty("toSrid").GetInt32().Should().Be(4326);

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        ((Point)geometry).X.Should().Be(0d);
        ((Point)geometry).Y.Should().Be(0d);
    }

    [UnitTest]
    public async Task ExecuteAsync_Wgs84ToWebMercator_ReprojectsLonLatToMercator()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-merc");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // POINT(0 0) in WGS 84 — projects to (0, 0) in Web Mercator (the prime meridian / equator origin).
        var record = CreateJobRecord(
            GeometryProjectJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: PointWkbBase64,
            fromSrid: 4326,
            toSrid: 3857);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();

        var bytes = Convert.FromBase64String(publishedUri!["data:application/geo+json;base64,".Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("properties").GetProperty("fromSrid").GetInt32().Should().Be(4326);
        doc.RootElement.GetProperty("properties").GetProperty("toSrid").GetInt32().Should().Be(3857);

        var geometry = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        var point = (Point)geometry;
        point.X.Should().BeApproximately(0d, 1e-3);
        point.Y.Should().BeApproximately(0d, 1e-3);
    }

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedTransformPair_FailsAsClassified()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-nad");

        var record = CreateJobRecord(
            GeometryProjectJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: PointWkbBase64,
            fromSrid: 4269,
            toSrid: 4326);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("not supported");
        result.ErrorMessage.Should().Contain("4269");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord(
            "geometry.intersect",
            includeInputs: false,
            wkb: null,
            fromSrid: 0,
            toSrid: 0);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.project");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            GeometryProjectJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: null,
            fromSrid: 4326,
            toSrid: 3857);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("wkb");
    }

    private static GeometryProjectJobExecutor CreateExecutor()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryProjectJobExecutor(monitor, NullLogger<GeometryProjectJobExecutor>.Instance);
    }

    private static ExecutionJobRecord CreateJobRecord(
        string? processId,
        bool includeInputs,
        string? wkb,
        int fromSrid,
        int toSrid)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(processId))
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId;
            parameters["protocolProcessId"] = processId;
        }

        if (includeInputs)
        {
            var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
            if (!string.IsNullOrEmpty(wkb))
            {
                parameters[prefix + "wkb"] = wkb;
            }
            parameters[prefix + "fromSrid"] = fromSrid.ToString(CultureInfo.InvariantCulture);
            parameters[prefix + "toSrid"] = toSrid.ToString(CultureInfo.InvariantCulture);
        }

        return new ExecutionJobRecord
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
    }
}
