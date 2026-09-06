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
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

[Collection("Database")]
[Trait("Category", "LayerExecutionProof")]
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
            _fixture.Postgres.ConnectionString,
            new LayerPublishRequest { Schema = _schema, Table = "sinkproof", LayerName = "Sink proof",
                GeometryColumn = "geom", PrimaryKey = "id", Srid = 4326, Fields = ["id", "attributes"],
                ServiceName = "sinkproof_" + Guid.NewGuid().ToString("N"), Enabled = true });
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
        rows.Should().HaveCount(3);
        AssertRow(rows, "A", 5, -5, 6, null);
        AssertRow(rows, "B", 24, 30, 40, "upsert-batch");
        AssertRow(rows, "C", 36, 50, 60, "upsert-batch");
    }

    [IntegrationTest]
    public async Task HonuaLayerSink_FailingRow_RollsBackKeyDeletionAndAllInsertedRows()
    {
        var failed = await Run("upsert", "failed-batch", """
            {"type":"FeatureCollection","features":[
            {"type":"Feature","geometry":{"type":"Point","coordinates":[70,80]},"properties":{"key":"A","value":70}},
            {"type":"Feature","geometry":{"type":"Point","coordinates":[11,22]},"properties":{"key":"bad","value":-1}}]}
            """);
        failed.Result.Status.Should().Be(ExecutionJobStatus.Failed);
        failed.Artifacts.Should().BeEmpty();
        var rows = await Read();
        rows.Should().ContainSingle();
        AssertRow(rows, "A", 5, -5, 6, null);
    }

    private async Task<(JobExecutionResult Result, List<string> Artifacts)> Run(string mode, string batch, string input)
    {
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new HonuaLayerSinkExecutor(options, NullLogger<HonuaLayerSinkExecutor>.Instance,
            new PostgresHonuaLayerSink(_fixture.Postgres.DataSource));
        var parameters = new Dictionary<string, string> { ["protocolProcessId"] = "sink.honua-layer",
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "sink.honua-layer" };
        foreach (var (key, value) in new[] { ("schema", _schema), ("layer", "sinkproof"), ("targetSrid", "4326"),
            ("loadMode", mode), ("batchId", batch), ("keyFields", "key"),
            ("input", "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(input))) })
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0." + key] = value;
        }
        var job = new ExecutionJobRecord { OperationId = batch, Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec { Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local", WorkloadName = "geoprocessing:sink.honua-layer", Parameters = parameters } };
        var artifacts = new List<string>();
        var context = Substitute.For<IJobExecutionContext>();
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(c => artifacts.Add(c.ArgAt<string>(0)));
        return (await executor.ExecuteAsync(job, context, CancellationToken.None), artifacts);
    }

    private async Task<Feature[]> Read()
    {
        var reader = _fixture.GetService<IFeatureReader>();
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
