// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
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
        using var securityScope = BeginAdminSubmitterScope();
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(
            Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink, EmptyCatalogScopeFactory());

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
        using var securityScope = BeginAdminSubmitterScope();
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(
            Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink, EmptyCatalogScopeFactory());

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
    public async Task HonuaLayerSink_SpilledStreamReferenceInput_IsAcceptedLikeInlineInput()
    {
        // server#4628: HonuaLayerSinkExecutor previously accepted only the inline
        // data:application/geo+json;base64 URI, so a workflow's transform-to-sink success
        // depended on whether the transform's output happened to cross
        // FeatureStreamPublisher's inline threshold. A honua-feature-stream:v1 reference
        // (the spilled shape) must load through identically.
        var sink = new CapturingLayerSink();
        var executorOptions = Options();
        var executor = new HonuaLayerSinkExecutor(executorOptions, NullLogger<HonuaLayerSinkExecutor>.Instance, sink);

        var spillPath = FeatureStreamArtifact.AllocateSpillPath(
            executorOptions.CurrentValue.OutputRootDirectory, "unit-op", HonuaLayerSinkExecutor.HandledProcessId);
        var streamReference = await FeatureStreamArtifact.WriteStreamAsync(
            spillPath,
            ToAsync(Feature(Point(5, 6), ("name", "spilled"))),
            CancellationToken.None);

        var (status, uri, _) = await RunAsync(
            executor,
            ("input", streamReference),
            ("layer", "parcels"),
            ("targetSrid", "4326"),
            ("batchId", "spill-batch"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        sink.Rows.Should().HaveCount(1);
        sink.Rows![0].AttributesJson.Should().Contain("\"name\":\"spilled\"");
        DecodeDescriptor(uri!).GetProperty("featuresWritten").GetInt64().Should().Be(1);
    }

    private static async IAsyncEnumerable<IFeature> ToAsync(params IFeature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
            await Task.CompletedTask;
        }
    }

    [UnitTest]
    public async Task HonuaLayerSink_UpsertWithKeyFields_PassesKeysToCapability()
    {
        using var securityScope = BeginAdminSubmitterScope();
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(
            Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink, EmptyCatalogScopeFactory());

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

    // -------------------------------------------------------------------------
    // #4625: destination authorization. sink.honua-layer accepted arbitrary
    // caller-supplied schema/table text with no target-specific authorization —
    // generic Process.Execute permission says nothing about a specific destination.
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task HonuaLayerSink_NoSubmitterSecurityContext_FailsClosed()
    {
        // No JobSecurityScope.Begin at all: mirrors source.honua-layer's fail-closed
        // contract (honua-server#3068) — a job that cannot be constrained to a submitter
        // must be refused rather than writing under an unconstrained identity.
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(
            Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink, EmptyCatalogScopeFactory());

        var (status, _, message) = await RunAsync(
            executor,
            ("input", BuildInputUri(Feature(Point(1, 2)))),
            ("layer", "parcels"),
            ("targetSrid", "4326"));

        status.Should().Be(ExecutionJobStatus.Failed);
        message.Should().Contain("submitter security context");
        sink.Request.Should().BeNull("authorization must run, and deny, before any load is attempted");
    }

    [UnitTest]
    public async Task HonuaLayerSink_NoAuthorizationScopeAvailable_FailsClosed()
    {
        // No IServiceScopeFactory supplied (the pre-#4625 constructor shape): there is no
        // way to evaluate anything, so this must deny rather than silently skip the check.
        using var securityScope = BeginAdminSubmitterScope();
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink);

        var (status, _, message) = await RunAsync(
            executor,
            ("input", BuildInputUri(Feature(Point(1, 2)))),
            ("layer", "parcels"),
            ("targetSrid", "4326"));

        status.Should().Be(ExecutionJobStatus.Failed);
        message.Should().Contain("no authorization scope is available");
        sink.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task HonuaLayerSink_NewDestination_NonAdminSubmitter_IsDenied()
    {
        // 'parcels' does not resolve to any existing catalog layer in the empty snapshot, so
        // this is a request to CREATE a brand-new table. An ordinary (non-admin) submitter
        // must not be able to conjure a new destination into existence (#4625 AC: "Govern
        // creation of new destinations separately").
        using var securityScope = BeginSubmitterScope(role: "editor");
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(
            Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink, EmptyCatalogScopeFactory());

        var (status, _, message) = await RunAsync(
            executor,
            ("input", BuildInputUri(Feature(Point(1, 2)))),
            ("layer", "parcels"),
            ("targetSrid", "4326"));

        status.Should().Be(ExecutionJobStatus.Failed);
        message.Should().Contain("administrative role");
        sink.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task HonuaLayerSink_NewDestination_AdminSubmitter_IsAuthorized()
    {
        using var securityScope = BeginAdminSubmitterScope();
        var sink = new CapturingLayerSink();
        var executor = new HonuaLayerSinkExecutor(
            Options(), NullLogger<HonuaLayerSinkExecutor>.Instance, sink, EmptyCatalogScopeFactory());

        var (status, _, _) = await RunAsync(
            executor,
            ("input", BuildInputUri(Feature(Point(1, 2)))),
            ("layer", "parcels"),
            ("targetSrid", "4326"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        sink.Request.Should().NotBeNull();
    }

    [UnitTest]
    public void FindExistingLayerId_MatchingSchemaAndTable_ReturnsStorageLayerId()
    {
        var snapshot = CatalogSnapshotWithRelationalLayer(layerId: 42, schema: "etl", table: "parcels");

        HonuaLayerSinkExecutor.FindExistingLayerId(snapshot, "etl", "parcels").Should().Be(42);
    }

    [UnitTest]
    public void FindExistingLayerId_DifferentTable_DoesNotMatch()
    {
        var snapshot = CatalogSnapshotWithRelationalLayer(layerId: 42, schema: "etl", table: "parcels");

        HonuaLayerSinkExecutor.FindExistingLayerId(snapshot, "etl", "other_table")
            .Should().BeNull("a different table name must never resolve to another layer's identity");
    }

    [UnitTest]
    public void FindExistingLayerId_DifferentSchema_DoesNotMatch()
    {
        var snapshot = CatalogSnapshotWithRelationalLayer(layerId: 42, schema: "etl", table: "parcels");

        HonuaLayerSinkExecutor.FindExistingLayerId(snapshot, "other_schema", "parcels")
            .Should().BeNull("a same-named table in a different schema must never resolve to another tenant's layer");
    }

    [UnitTest]
    public void FindExistingLayerId_CaseMismatch_DoesNotMatch()
    {
        // Postgres identifiers here are always double-quoted (case-sensitive literal); a
        // case-mismatched request must not be treated as the same destination.
        var snapshot = CatalogSnapshotWithRelationalLayer(layerId: 42, schema: "etl", table: "parcels");

        HonuaLayerSinkExecutor.FindExistingLayerId(snapshot, "ETL", "Parcels").Should().BeNull();
    }

    [UnitTest]
    public void FindExistingLayerId_EmptyCatalog_ReturnsNull()
    {
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph { Revision = 1 }, "\"empty\"", DateTimeOffset.UnixEpoch);

        HonuaLayerSinkExecutor.FindExistingLayerId(snapshot, "etl", "parcels").Should().BeNull();
    }

    private static MetadataV2GraphSnapshot CatalogSnapshotWithRelationalLayer(int layerId, string schema, string table)
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"res-{layerId}", Name = $"layer-{layerId}" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"binding-{layerId}", Name = $"binding-{layerId}" },
            ResourceId = resource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"{schema}.{table}",
            StorageLayerId = layerId,
        };

        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Resources = [resource],
            StorageBindings = [binding],
        };
        return new MetadataV2GraphSnapshot(graph, "\"sink-tests\"", DateTimeOffset.UnixEpoch);
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

    /// <summary>
    /// Builds an <see cref="IServiceScopeFactory"/> whose scope resolves an
    /// <see cref="IMetadataV2GraphProvider"/> over an EMPTY catalog (no resources, no storage
    /// bindings) — every destination is therefore a "new destination" from
    /// <see cref="HonuaLayerSinkExecutor.FindExistingLayerId"/>'s point of view.
    /// </summary>
    private static IServiceScopeFactory EmptyCatalogScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(new EmptyMetadataV2GraphProvider());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class EmptyMetadataV2GraphProvider : IMetadataV2GraphProvider
    {
        private readonly MetadataV2GraphSnapshot _snapshot =
            new(new MetadataV2Graph { Revision = 1 }, "\"empty\"", DateTimeOffset.UnixEpoch);

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(revision == _snapshot.Revision ? _snapshot : null);
    }

    /// <summary>Begins a job security scope for a submitter carrying the "admin" role claim.</summary>
    private static IDisposable BeginAdminSubmitterScope() => BeginSubmitterScope("admin");

    /// <summary>Begins a job security scope for a submitter carrying the given role claim.</summary>
    private static IDisposable BeginSubmitterScope(string role) => JobSecurityScope.Begin(
        new JobSecurityContext(
            PrincipalId: "test-submitter",
            TenantId: null,
            Claims: [new JobSecurityClaim(ClaimTypes.Role, role)]));

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
            // Path.Combine args are a temp-dir root plus a literal relative folder name; no rooted-segment risk.
            OutputRootDirectory = Path.Join(Path.GetTempPath(), "honua-geoprocessing-outputs")
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
