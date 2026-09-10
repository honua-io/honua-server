// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.ControlPlane;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;
using NSubstitute;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

[Collection("Database")]
[Trait("Category", "LayerExecutionProof")]
// These contracts exercise shared executors and storage directly, without an HTTP adapter.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.ContractTesting)]
public sealed class LayerSinkExecutionProofTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureServices(_ => { });
    private int _layerId;
    private string _schema = "";

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _schema = _fixture.CurrentSchema!;
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($$"""
            CREATE TABLE "{{_schema}}".sinkproof (
                id bigserial PRIMARY KEY, geom geometry(Geometry,4326), attributes jsonb NOT NULL,
                CHECK ((attributes->>'value')::integer >= 0));
            INSERT INTO "{{_schema}}".sinkproof (geom,attributes)
            VALUES (ST_SetSRID(ST_MakePoint(-5,6),4326), '{"key":"A","value":5}');
            """, connection);
        await command.ExecuteNonQueryAsync();
        var layer = await _fixture.GetService<ILayerPublishingService>().PublishLayerAsync(
            new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString) { SearchPath = _schema + ",public" }.ConnectionString,
            new LayerPublishRequest
            {
                Schema = _schema,
                Table = "sinkproof",
                LayerName = "Sink proof",
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Srid = 4326,
                Fields = ["id", "attributes"],
                ServiceName = "sinkproof_" + Guid.NewGuid().ToString("N"),
                Enabled = true
            });
        _layerId = layer.LayerId;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    public async Task HonuaLayerSink_AppendThenKeyedUpsert_ReadsExactCommittedGeometryAttributesAndReceipts()
    {
        var appended = await Run("append", "append-batch", """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","geometry":{"type":"Point","coordinates":[10,20]},"properties":{"key":"B","value":12}},
            {"type":"Feature","geometry":null,"properties":{"key":"rejected","value":99}}]}
            """);
        AssertReceipt(appended, 1, 1, "Append", "append-batch");
        var rows = await Read();
        rows.Should().HaveCount(2);
        AssertRow(rows, "A", 5, -5, 6, null);
        AssertRow(rows, "B", 12, 10, 20, "append-batch");

        var upserted = await Run("upsert", "upsert-batch", """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","geometry":{"type":"Point","coordinates":[30,40]},"properties":{"key":"B","value":24}},
            {"type":"Feature","geometry":{"type":"Point","coordinates":[50,60]},"properties":{"key":"C","value":36}}]}
            """);
        AssertReceipt(upserted, 2, 0, "Upsert", "upsert-batch");
        rows = await Read();
        AssertUpsertedRows(rows);

        // A successful append in place of keyed upsert produces valid persisted
        // features and a success receipt, but must fail the same read-back oracle.
        var wrongMode = await Run("append", "wrong-mode-batch", """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","geometry":{"type":"Point","coordinates":[30,40]},"properties":{"key":"B","value":24}}]}
            """);
        AssertReceipt(wrongMode, 1, 0, "Append", "wrong-mode-batch");
        var wrongRows = await Read();
        Action assertWrongMode = () => AssertUpsertedRows(wrongRows);
        assertWrongMode.Should().Throw<XunitException>();

        var b = rows.Single(f => Attributes(f).GetProperty("key").GetString() == "B");
        var wrongValue = b with
        {
            Attributes = b.Attributes.SetItem("attributes",
            JsonSerializer.SerializeToElement(new { key = "B", value = 12, __pipeline_batch_id = "upsert-batch" }))
        };
        var wrongGeometry = b with { Geometry = new WKTReader().Read("POINT (40 30)").AsBinary() };
        foreach (var corrupted in new[] { wrongValue, wrongGeometry })
        {
            var corruptedRows = rows.Select(f => f.Id == b.Id ? corrupted : f).ToArray();
            Action assert = () => AssertUpsertedRows(corruptedRows);
            assert.Should().Throw<XunitException>();
        }
    }

    [IntegrationTest]
    public async Task HonuaLayerSink_FailingRow_RollsBackKeyDeletionAndAllInsertedRows()
    {
        var original = (await Read()).Should().ContainSingle().Which;
        var failed = await Run("upsert", "failed-batch", """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","geometry":{"type":"Point","coordinates":[70,80]},"properties":{"key":"A","value":70}},
            {"type":"Feature","geometry":{"type":"Point","coordinates":[11,22]},"properties":{"key":"bad","value":-1}}]}
            """);
        failed.Result.Status.Should().Be(ExecutionJobStatus.Failed);
        failed.Result.ErrorMessage.Should().Be("sink.honua-layer load failed: PostgresException.");
        failed.Artifacts.Should().BeEmpty();
        var rows = await Read();
        rows.Should().ContainSingle().Which.Id.Should().Be(original.Id);
        AssertRow(rows, "A", 5, -5, 6, null);
    }

    [IntegrationTest]
    public async Task HonuaLayerSink_ReplayOfSameBatchIdAfterCommit_ReturnsSameReceiptWithoutDuplicatingRows()
    {
        // server#4626: a retry that replays an already-committed batchId (the crash/retry
        // scenario — process death after commit, before the job reaches a terminal state)
        // must reconstruct the original receipt rather than re-appending the rows a second
        // time. The oracle here is independently computed: exactly one row for key "R" is
        // possible after N replays only if the second (and any further) attempt is a no-op.
        var first = await Run("append", "replay-batch", """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","geometry":{"type":"Point","coordinates":[15,25]},"properties":{"key":"R","value":7}}]}
            """);
        AssertReceipt(first, 1, 0, "Append", "replay-batch");

        var replay = await Run("append", "replay-batch", """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","geometry":{"type":"Point","coordinates":[15,25]},"properties":{"key":"R","value":7}}]}
            """);
        // Same receipt reconstructed from the durable commit record, not a second write.
        AssertReceipt(replay, 1, 0, "Append", "replay-batch");

        var rows = await Read();
        rows.Should().ContainSingle(f => Attributes(f).GetProperty("key").GetString() == "R");
    }

    [IntegrationTest]
    public async Task HonuaLayerSink_SpilledStreamInput_LoadsIdenticalContentToInlineEquivalent()
    {
        // server#4628: FeatureStreamPublisher spills a transform's output to a
        // honua-feature-stream reference once it crosses the inline threshold; the sink must
        // accept that reference (not only the inline data-URI shape) and load the same
        // content. The oracle is independently computed from the source features written to
        // the spill file, not a snapshot of executor output.
        var options = new GeoprocessingExecutorOptions();
        var features = new NetTopologySuite.Features.IFeature[]
        {
            new NetTopologySuite.Features.Feature(Point(90, -10), Attrs(("key", "S1"), ("value", 100))),
            new NetTopologySuite.Features.Feature(Point(91, -11), Attrs(("key", "S2"), ("value", 200))),
            new NetTopologySuite.Features.Feature(null!, Attrs(("key", "rejected"), ("value", -1))),
        };
        var spillPath = FeatureStreamArtifact.AllocateSpillPath(
            options.OutputRootDirectory, "spill-op", "sink.honua-layer");
        var streamReference = await FeatureStreamArtifact.WriteStreamAsync(
            spillPath, ToAsync(features), CancellationToken.None);
        FeatureStreamArtifact.IsStreamReference(streamReference).Should().BeTrue();

        var result = await RunWithInputUri("append", "spill-batch", streamReference, options);

        AssertReceipt(result, 2, 1, "Append", "spill-batch");
        var rows = await Read();
        AssertRow(rows, "S1", 100, 90, -10, "spill-batch");
        AssertRow(rows, "S2", 200, 91, -11, "spill-batch");
    }

    private static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

    private static NetTopologySuite.Features.AttributesTable Attrs(params (string Name, object Value)[] values)
    {
        var table = new NetTopologySuite.Features.AttributesTable();
        foreach (var (name, value) in values)
        {
            table.Add(name, value);
        }

        return table;
    }

    private static async IAsyncEnumerable<NetTopologySuite.Features.IFeature> ToAsync(
        IEnumerable<NetTopologySuite.Features.IFeature> features)
    {
        foreach (var feature in features)
        {
            yield return feature;
            await Task.CompletedTask;
        }
    }

    private static void AssertUpsertedRows(Feature[] rows)
    {
        // Report the scalar count: formatting an entire Feature on failure walks
        // default ImmutableArray metadata unrelated to this persistence oracle.
        rows.Length.Should().Be(3);
        AssertRow(rows, "A", 5, -5, 6, null);
        AssertRow(rows, "B", 24, 30, 40, "upsert-batch");
        AssertRow(rows, "C", 36, 50, 60, "upsert-batch");
    }

    private Task<(JobExecutionResult Result, List<string> Artifacts)> Run(string mode, string batch, string input)
        => RunWithInputUri(
            mode, batch, "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(input)));

    private async Task<(JobExecutionResult Result, List<string> Artifacts)> RunWithInputUri(
        string mode, string batch, string inputUri, GeoprocessingExecutorOptions? executorOptions = null)
    {
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(executorOptions ?? new GeoprocessingExecutorOptions());
        var executor = new HonuaLayerSinkExecutor(options, NullLogger<HonuaLayerSinkExecutor>.Instance,
            new PostgresHonuaLayerSink(_fixture.Postgres.DataSource));
        var parameters = new Dictionary<string, string>
        {
            ["protocolProcessId"] = "sink.honua-layer",
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "sink.honua-layer"
        };
        foreach (var (key, value) in new[] { ("schema", _schema), ("layer", "sinkproof"), ("targetSrid", "4326"),
            ("loadMode", mode), ("batchId", batch), ("keyFields", "key"),
            ("input", inputUri) })
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0." + key] = value;
        }
        var job = new ExecutionJobRecord
        {
            OperationId = batch,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:sink.honua-layer",
                Parameters = parameters
            }
        };
        var artifacts = new List<string>();
        var context = Substitute.For<IJobExecutionContext>();
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(c => artifacts.Add(c.ArgAt<string>(0)));
        return (await executor.ExecuteAsync(job, context, CancellationToken.None), artifacts);
    }

    private async Task<Feature[]> Read()
    {
        // WKB query output need not carry EWKB SRID metadata. Check the physical
        // PostGIS geometry SRID separately from the canonical content read-back.
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".sinkproof WHERE geom IS NULL OR ST_SRID(geom) <> 4326", connection);
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(0);
        var snapshot = await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var resource = snapshot.Index.ResourcesByStorageLayerId[_layerId];
        var publication = snapshot.Graph.Publications.First(p => p.ResourceId == resource.Metadata.Id && snapshot.IsRoutable(p));
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        var reader = await _fixture.GetService<FeatureProviderQueryRouter>().ResolveReaderAsync(
            snapshot, service, resource, publication, _layerId, FeatureProviderReadOperation.Query);
        reader.GetType().Assembly.GetName().Name.Should().Be("Honua.Postgres");
        return (await reader.QueryAsync(_layerId, new FeatureQuery())).Items.ToArray();
    }

    private static JsonElement Attributes(Feature feature)
    {
        var value = feature.Attributes["attributes"];
        using var json = JsonDocument.Parse(value is JsonElement element ? element.GetRawText() : value!.ToString()!);
        return json.RootElement.Clone();
    }

    private static void AssertRow(Feature[] rows, string key, int value, double x, double y, string? batch)
    {
        var row = rows.Should().ContainSingle(f => Attributes(f).GetProperty("key").GetString() == key).Which;
        var properties = Attributes(row);
        properties.GetProperty("value").GetInt32().Should().Be(value);
        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            batch is null ? ["key", "value"] : new[] { "key", "value", "__pipeline_batch_id" });
        if (batch is not null)
        {
            properties.GetProperty("__pipeline_batch_id").GetString().Should().Be(batch);
        }
        var geometry = new WKBReader().Read(row.Geometry!);
        geometry.GeometryType.Should().Be("Point");
        geometry.Coordinate.X.Should().Be(x);
        geometry.Coordinate.Y.Should().Be(y);
    }

    private static void AssertReceipt((JobExecutionResult Result, List<string> Artifacts) run, long written, long rejected, string mode, string batch)
    {
        run.Result.Status.Should().Be(ExecutionJobStatus.Succeeded, run.Result.ErrorMessage);
        run.Artifacts.Should().ContainSingle();
        using var receipt = JsonDocument.Parse(Convert.FromBase64String(run.Artifacts[0][(run.Artifacts[0].IndexOf(',') + 1)..]));
        receipt.RootElement.GetProperty("featuresWritten").GetInt64().Should().Be(written);
        receipt.RootElement.GetProperty("featuresRejected").GetInt64().Should().Be(rejected);
        receipt.RootElement.GetProperty("loadMode").GetString().Should().Be(mode);
        receipt.RootElement.GetProperty("batchId").GetString().Should().Be(batch);
    }
}
