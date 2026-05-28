// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage of the slice-5 <c>geometry.dissolve</c> executor. Mirrors
/// the failure surfaces the union executor pins (since dissolve uses the
/// same WkbArray input contract) plus the new group-aware behavior: two
/// overlapping boxes tagged with the same key collapse into one feature,
/// while two distinct keys yield a FeatureCollection with one feature per
/// group.
/// </summary>
public sealed class GeometryDissolveJobExecutorTests
{
    private const string FeatureDataUriPrefix = "data:application/geo+json;base64,";

    private static readonly string[] KeysAA = { "a", "a" };
    private static readonly string[] KeysAB = { "a", "b" };
    private static readonly string[] KeysOnlyOne = { "only-one" };
    private static readonly string[] ExpectedKeysAB = { "a", "b" };

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord(processId: "geometry.buffer", includeInputs: false);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("geometry.dissolve");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_SingleGroup_CollapsesIntoOneFeature()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-one-group");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Two overlapping 10x10 boxes share the same group key -> union area 175.
        var wkbs = BuildWkbsJson(
            BuildBox(0, 0, 10, 10),
            BuildBox(5, 5, 15, 15));
        var keys = JsonSerializer.Serialize(KeysAA);

        var record = CreateJobRecord(
            processId: GeometryDissolveJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: wkbs,
            groupKeysJson: keys);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();
        publishedUri!.Should().StartWith(FeatureDataUriPrefix);

        var bytes = Convert.FromBase64String(publishedUri[FeatureDataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        doc.RootElement.GetProperty("processId").GetString().Should().Be("geometry.dissolve");
        doc.RootElement.GetProperty("inputSrid").GetInt32().Should().Be(4326);
        doc.RootElement.GetProperty("inputCount").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("groupCount").GetInt32().Should().Be(1);

        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);

        var feature = features[0];
        feature.GetProperty("properties").GetProperty("groupKey").GetString().Should().Be("a");
        var geometry = new GeoJsonReader().Read<Geometry>(
            feature.GetProperty("geometry").GetRawText());
        geometry.Area.Should().BeApproximately(175.0, 1e-6,
            "two overlapping 10x10 boxes under one group dissolve to an L with area 175");
    }

    [UnitTest]
    public async Task ExecuteAsync_TwoGroups_EmitsFeaturePerGroup()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-two-groups");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        // Two non-overlapping boxes with distinct keys -> two features of
        // area 100 each, in input order ("a" then "b").
        var wkbs = BuildWkbsJson(
            BuildBox(0, 0, 10, 10),
            BuildBox(20, 20, 30, 30));
        var keys = JsonSerializer.Serialize(KeysAB);

        var record = CreateJobRecord(
            processId: GeometryDissolveJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: wkbs,
            groupKeysJson: keys);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        publishedUri.Should().NotBeNull();

        var bytes = Convert.FromBase64String(publishedUri!["data:application/geo+json;base64,".Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("groupCount").GetInt32().Should().Be(2);
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(2);

        var keys0 = features[0].GetProperty("properties").GetProperty("groupKey").GetString();
        var keys1 = features[1].GetProperty("properties").GetProperty("groupKey").GetString();
        new[] { keys0, keys1 }.Should().BeEquivalentTo(ExpectedKeysAB);
    }

    [UnitTest]
    public async Task ExecuteAsync_WithoutGroupKeys_CollapsesAllToSingleFeature()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-no-keys");

        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var wkbs = BuildWkbsJson(
            BuildBox(0, 0, 10, 10),
            BuildBox(5, 5, 15, 15));

        var record = CreateJobRecord(
            processId: GeometryDissolveJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: wkbs,
            groupKeysJson: null);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);

        var bytes = Convert.FromBase64String(publishedUri!["data:application/geo+json;base64,".Length..]);
        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("groupCount").GetInt32().Should().Be(1);
        var feature = doc.RootElement.GetProperty("features")[0];
        feature.GetProperty("properties").GetProperty("groupKey").GetString().Should().Be("__all__");
    }

    [UnitTest]
    public async Task ExecuteAsync_GroupKeysLengthMismatch_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-mismatch");

        var wkbs = BuildWkbsJson(
            BuildBox(0, 0, 10, 10),
            BuildBox(5, 5, 15, 15));
        var keys = JsonSerializer.Serialize(KeysOnlyOne);

        var record = CreateJobRecord(
            processId: GeometryDissolveJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: wkbs,
            groupKeysJson: keys);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("groupKeys");
        result.ErrorMessage.Should().Contain("length");
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingWkbs_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-missing");

        var record = CreateJobRecord(
            processId: GeometryDissolveJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: null);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'wkbs'");
    }

    [UnitTest]
    public async Task ExecuteAsync_EmptyArray_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-empty");

        var record = CreateJobRecord(
            processId: GeometryDissolveJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: "[]");

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("at least one");
    }

    [UnitTest]
    public async Task ExecuteAsync_PayloadExceedsMaxArtifactBytes_FailsWithGuardrail()
    {
        var executor = CreateExecutor(maxArtifactBytes: 32);
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-too-big");

        var wkbs = BuildWkbsJson(
            BuildBox(0, 0, 10, 10),
            BuildBox(5, 5, 15, 15));

        var record = CreateJobRecord(
            processId: GeometryDissolveJobExecutor.HandledProcessId,
            includeInputs: true,
            wkbsJson: wkbs);

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static GeometryDissolveJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new GeometryDissolveJobExecutor(monitor, NullLogger<GeometryDissolveJobExecutor>.Instance);
    }

    private static Polygon BuildBox(double minX, double minY, double maxX, double maxY)
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

    private static string BuildWkbsJson(params Geometry[] geometries)
    {
        var writer = new WKBWriter();
        var items = geometries.Select(g => Convert.ToBase64String(writer.Write(g)));
        return JsonSerializer.Serialize(items);
    }

    private static ExecutionJobRecord CreateJobRecord(
        string? processId,
        bool includeInputs,
        string? wkbsJson = null,
        string? srid = "4326",
        string? groupKeysJson = null)
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
            if (wkbsJson != null)
            {
                parameters[prefix + "wkbs"] = wkbsJson;
            }
            parameters[prefix + "srid"] = srid ?? "4326";
            if (groupKeysJson != null)
            {
                parameters[prefix + "groupKeys"] = groupKeysJson;
            }
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
