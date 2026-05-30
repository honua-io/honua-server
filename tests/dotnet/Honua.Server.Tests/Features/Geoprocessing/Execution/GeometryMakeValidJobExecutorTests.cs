// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
/// Unit coverage for the reconciled <c>geometry.make-valid</c> executor — the
/// managed NetTopologySuite GeometryFixer repair that closes a catalog-honesty
/// gap (advertised, sync-eligible, but previously unrouted on trunk).
/// </summary>
public sealed class GeometryMakeValidJobExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord("geometry.buffer", includeInputs: false);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.make-valid");
    }

    [UnitTest]
    public async Task ExecuteAsync_SelfIntersectingBowtie_RepairsToValidGeometry()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-bowtie");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Classic self-intersecting "bowtie" polygon — invalid until repaired.
        var bowtie = BuildBowtie();
        bowtie.IsValid.Should().BeFalse("the fixture must be invalid for the test to be meaningful");

        var record = CreateJobRecord(
            GeometryMakeValidJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(bowtie));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();

        var bytes = Convert.FromBase64String(publishedUri![DataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("properties").GetProperty("processId").GetString()
            .Should().Be("geometry.make-valid");
        doc.RootElement.GetProperty("properties").GetProperty("inputWasValid").GetBoolean()
            .Should().BeFalse();

        var repaired = new GeoJsonReader().Read<Geometry>(
            doc.RootElement.GetProperty("geometry").GetRawText());
        repaired.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public async Task ExecuteAsync_AlreadyValidGeometry_PassesThroughMarkedValid()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-valid");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var record = CreateJobRecord(
            GeometryMakeValidJobExecutor.HandledProcessId,
            includeInputs: true,
            wkb: WkbBase64(BuildBoxPolygon(0, 0, 10, 10)));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var bytes = Convert.FromBase64String(publishedUri![DataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("properties").GetProperty("inputWasValid").GetBoolean()
            .Should().BeTrue();
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingWkb_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            GeometryMakeValidJobExecutor.HandledProcessId,
            includeInputs: true,
            omitWkb: true);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("wkb");
    }

    private static GeometryMakeValidJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryMakeValidJobExecutor(monitor, NullLogger<GeometryMakeValidJobExecutor>.Instance);
    }

    private static Polygon BuildBoxPolygon(double minX, double minY, double maxX, double maxY)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        });
    }

    private static Polygon BuildBowtie()
    {
        // A ring that crosses itself between (0,0)->(1,1) and (0,1)->(1,0).
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
        return factory.CreatePolygon(new[]
        {
            new Coordinate(0, 0),
            new Coordinate(1, 1),
            new Coordinate(0, 1),
            new Coordinate(1, 0),
            new Coordinate(0, 0),
        });
    }

    private static string WkbBase64(Geometry geometry)
        => Convert.ToBase64String(new WKBWriter().Write(geometry));

    private static ExecutionJobRecord CreateJobRecord(
        string? processId,
        bool includeInputs,
        string? wkb = null,
        bool omitWkb = false)
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
            if (!omitWkb)
            {
                parameters[prefix + "wkb"] = wkb ?? string.Empty;
            }
            parameters[prefix + "srid"] = "4326";
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
