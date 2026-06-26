// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the catalog honua-layer sink executor (#2210). Proves the
/// seam: the node fails closed with a clear message when the optional
/// <see cref="IHonuaLayerSink"/> capability is absent (a lean, Postgres-free deployment),
/// and otherwise loads pre-encoded rows through the capability with the requested load mode.
/// </summary>
public sealed class HonuaLayerSinkExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task HonuaLayerSink_CapabilityAbsent_FailsClosedWithClearMessage()
    {
        // Lean deployment: no catalog database ⇒ IHonuaLayerSink is not registered.
        var executor = new HonuaLayerSinkExecutor(Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink: null);

        var (status, _, message) = await RunAsync(
            executor,
            ("input", BuildInputUri(Feature(Point(1, 2)))),
            ("layer", "parcels"),
            ("targetSrid", "4326"));

        status.Should().Be(ExecutionJobStatus.Failed);
        message.Should().Contain("unavailable in this deployment");
    }

    [UnitTest]
    public async Task HonuaLayerSink_LoadsRowsThroughCapability_AppendMode()
    {
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink);

        var (status, uri, _) = await RunAsync(
            executor,
            ("input", BuildInputUri(
                Feature(Point(1, 2), ("name", "a")),
                Feature(Point(3, 4), ("name", "b")))),
            ("layer", "parcels"),
            ("schema", "etl"),
            ("targetSrid", "3857"),
            ("batchId", "batch-9"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        sink.Request.Should().NotBeNull();
        sink.Request!.Schema.Should().Be("etl");
        sink.Request.Table.Should().Be("parcels");
        sink.Request.TargetSrid.Should().Be(3857);
        sink.Request.LoadMode.Should().Be(HonuaLayerLoadMode.Append);
        sink.Request.BatchId.Should().Be("batch-9");
        sink.Rows.Should().HaveCount(2);
        // Every row's attributes JSON carries the reserved batch-id key for rollback.
        sink.Rows!.Should().OnlyContain(r => r.AttributesJson.Contains("__pipeline_batch_id"));

        var descriptor = DecodeDescriptor(uri!);
        descriptor.GetProperty("loadMode").GetString().Should().Be("Append");
        descriptor.GetProperty("featuresWritten").GetInt64().Should().Be(2);
    }

    [UnitTest]
    public async Task HonuaLayerSink_NullGeometryRows_AreRejectedNotLoaded()
    {
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink);

        var (status, uri, _) = await RunAsync(
            executor,
            ("input", BuildInputUri(
                Feature(Point(1, 2)),
                new Feature(null!, new AttributesTable()))),
            ("layer", "parcels"),
            ("targetSrid", "4326"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        sink.Rows.Should().HaveCount(1);
        DecodeDescriptor(uri!).GetProperty("featuresRejected").GetInt64().Should().Be(1);
    }

    [UnitTest]
    public async Task HonuaLayerSink_UpsertWithoutKeyFields_FailsValidation()
    {
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink);

        var (status, _, message) = await RunAsync(
            executor,
            ("input", BuildInputUri(Feature(Point(1, 2)))),
            ("layer", "parcels"),
            ("targetSrid", "4326"),
            ("loadMode", "upsert"));

        status.Should().Be(ExecutionJobStatus.Failed);
        message.Should().Contain("keyFields");
        sink.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task HonuaLayerSink_InvalidLoadMode_FailsValidation()
    {
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink);

        var (status, _, _) = await RunAsync(
            executor,
            ("input", BuildInputUri(Feature(Point(1, 2)))),
            ("layer", "parcels"),
            ("targetSrid", "4326"),
            ("loadMode", "merge"));

        status.Should().Be(ExecutionJobStatus.Failed);
        sink.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task HonuaLayerSink_UpsertWithKeyFields_PassesKeysToCapability()
    {
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink);

        var (status, _, _) = await RunAsync(
            executor,
            ("input", BuildInputUri(Feature(Point(1, 2), ("gid", "x")))),
            ("layer", "parcels"),
            ("targetSrid", "4326"),
            ("loadMode", "upsert"),
            ("keyFields", "gid, region"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        sink.Request!.LoadMode.Should().Be(HonuaLayerLoadMode.Upsert);
        sink.Request.KeyFields.Should().Equal("gid", "region");
    }

    private sealed class CapturingLayerSink : IHonuaLayerSink
    {
        public HonuaLayerSinkRequest? Request { get; private set; }

        public IReadOnlyList<HonuaLayerSinkRow>? Rows { get; private set; }

        public Task<HonuaLayerSinkOutcome> LoadAsync(
            HonuaLayerSinkRequest request,
            IReadOnlyList<HonuaLayerSinkRow> rows,
            CancellationToken cancellationToken)
        {
            Request = request;
            Rows = rows;
            return Task.FromResult(new HonuaLayerSinkOutcome(rows.Count, request.Schema, request.Table, request.BatchId));
        }
    }

    private static JsonElement DecodeDescriptor(string uri)
    {
        const string prefix = "data:application/json;base64,";
        uri.Should().StartWith(prefix);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(uri[prefix.Length..]));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7),
            OutputRootDirectory = Path.Combine(Path.GetTempPath(), "honua-geoprocessing-outputs")
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

    private static async Task<(ExecutionJobStatus Status, string? Uri, string Message)> RunAsync(
        HonuaLayerSinkExecutor executor,
        params (string Name, string Value)[] inputs)
    {
        const string processId = HonuaLayerSinkExecutor.HandledProcessId;
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
        return (result.Status, publishedUri, result.ErrorMessage ?? string.Empty);
    }
}
