// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit;
using NetTopologySuite.IO;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

[Collection("Database")]
[Trait("Category", "CopyFeaturesExecutionProof")]
public sealed class CopyFeaturesExecutionProofTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureServices(_ => { });
    private int _sourceId;
    private MetadataV2Resource _sourceResource = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var schema = _fixture.CurrentSchema!;
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE TABLE "{schema}".copyproof (
                id bigint PRIMARY KEY, label varchar(32) NOT NULL, score integer NOT NULL,
                note text, geom geometry(PointZ,4326));
            INSERT INTO "{schema}".copyproof VALUES
                (11, 'alpha', 7, 'retained', ST_GeomFromEWKT('SRID=4326;POINT Z (12 34 56)')),
                (13, 'beta', 14, NULL, ST_GeomFromEWKT('SRID=4326;POINT Z (-20 40 80)')),
                (15, 'gamma', 21, 'third', ST_GeomFromEWKT('SRID=4326;POINT Z (30 -10 90)'));
            """, connection);
        await command.ExecuteNonQueryAsync();
        var published = await _fixture.GetService<ILayerPublishingService>().PublishLayerAsync(
            new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString) { SearchPath = schema + ",public" }.ConnectionString,
            new LayerPublishRequest
            {
                Schema = schema,
                Table = "copyproof",
                LayerName = "Copy proof source",
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Srid = 4326,
                Fields = ["id", "label", "score", "note"],
                ServiceName = "copyproof_" + Guid.NewGuid().ToString("N"),
                Enabled = true
            });
        _sourceId = published.LayerId;
        _sourceResource = (await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync())
            .Index.ResourcesByStorageLayerId[_sourceId];
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Theory]
    [InlineData(null, null, new long[] { 11, 13, 15 })]
    [InlineData("score >= 14", null, new long[] { 13, 15 })]
    [InlineData("score >= 14", "11,15", new long[] { 15 })]
    public async Task CopyFeatures_RealPublishedLayer_PreservesSelectedValuesZSchemaSridAndProvenance(
        string? where, string? objectIds, long[] expectedIds)
    {
        var executor = _fixture.GetService<CopyFeaturesExecutor>();
        _fixture.GetService<IEnumerable<IProcessExecutor>>().Should().Contain(executor);
        var operationId = Guid.NewGuid().ToString("N");
        var name = "Independent copy " + operationId;
        var parameters = new Dictionary<string, string>
        {
            ["protocolProcessId"] = "data-management.copy-features",
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "data-management.copy-features"
        };
        foreach (var (key, value) in new[] { ("sourceLayerId", _sourceId.ToString(CultureInfo.InvariantCulture)), ("targetLayerName", name),
            ("where", where), ("objectIds", objectIds) })
        {
            if (value is not null)
            {
                parameters[ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0." + key] = value;
            }
        }
        var job = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:data-management.copy-features",
                Parameters = parameters
            }
        };
        var artifacts = new List<string>();
        var context = Substitute.For<IJobExecutionContext>();
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(c => artifacts.Add(c.ArgAt<string>(0)));
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        artifacts.Should().ContainSingle();
        using var receipt = JsonDocument.Parse(Convert.FromBase64String(artifacts[0][(artifacts[0].IndexOf(',') + 1)..]));
        var root = receipt.RootElement;
        var targetId = root.GetProperty("layerId").GetInt32();
        targetId.Should().NotBe(_sourceId);
        root.GetProperty("sourceLayerId").GetInt32().Should().Be(_sourceId);
        root.GetProperty("featureCount").GetInt64().Should().Be(expectedIds.Length);
        root.GetProperty("srid").GetInt32().Should().Be(4326);
        root.GetProperty("operationId").GetString().Should().Be(operationId);
        var snapshot = await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var target = snapshot.Index.ResourcesByStorageLayerId[targetId];
        target.SchemaFields.Should().BeEquivalentTo(_sourceResource.SchemaFields);
        target.SchemaFields.Single(f => f.Name == "score").Type.Should().Be(MetadataV2FieldType.Integer);
        target.SchemaFields.Single(f => f.Name == "label").Length.Should().Be(32);
        target.SchemaFields.Single(f => f.Name == "note").Nullable.Should().BeTrue();
        target.Spatial!.SpatialReference!.ResolveSrid().Should().Be(4326);
        target.Metadata.Name.Should().Be(name);
        target.Metadata.Annotations["gp.operationId"].Should().Be(operationId);
        target.Metadata.Annotations["gp.sourceLayerId"].Should().Be(_sourceId.ToString(CultureInfo.InvariantCulture));
        target.Metadata.Annotations["gp.processId"].Should().Be("data-management.copy-features");
        snapshot.Index.ResourcesByStorageLayerId[_sourceId].Should().BeEquivalentTo(_sourceResource);
        await AssertRows(targetId, expectedIds);
        // The oracle is the literal fixture, never the copy's own output or a snapshot.
        await AssertRows(_sourceId, [11, 13, 15]);
    }

    private async Task AssertRows(int layerId, long[] expectedIds)
    {
        var snapshot = await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var resource = snapshot.Index.ResourcesByStorageLayerId[layerId];
        var publication = snapshot.Graph.Publications.First(p => p.ResourceId == resource.Metadata.Id && snapshot.IsRoutable(p));
        var reader = await _fixture.GetService<FeatureProviderQueryRouter>().ResolveReaderAsync(snapshot,
            snapshot.Index.ServicesById[publication.ServiceId], resource, publication, layerId, FeatureProviderReadOperation.Query);
        reader.GetType().Assembly.GetName().Name.Should().Be("Honua.Postgres");
        var rows = (await reader.QueryAsync(layerId, new FeatureQuery { IncludeZ = true, IncludeM = true })).Items;
        rows.Select(f => f.Id).Should().BeEquivalentTo(expectedIds);
        foreach (var row in rows)
        {
            var expected = row.Id switch
            {
                11 => ("alpha", 7, "retained", 12d, 34d, 56d),
                13 => ("beta", 14, (string?)null, -20d, 40d, 80d),
                15 => ("gamma", 21, "third", 30d, -10d, 90d),
                _ => throw new InvalidOperationException("Unexpected row")
            };
            row.Attributes.Keys.Should().BeEquivalentTo("id", "objectid", "label", "score", "note");
            Convert.ToInt64(row.Attributes["id"], CultureInfo.InvariantCulture).Should().Be(row.Id);
            // The canonical reader adds its public objectid alias to the typed id.
            Convert.ToInt64(row.Attributes["objectid"], CultureInfo.InvariantCulture).Should().Be(row.Id);
            row.Attributes["label"].Should().Be(expected.Item1);
            Convert.ToInt32(row.Attributes["score"], CultureInfo.InvariantCulture).Should().Be(expected.Item2);
            row.Attributes["note"].Should().Be(expected.Item3);
            var geometry = new WKBReader().Read(row.Geometry!);
            geometry.GeometryType.Should().Be("Point");
            geometry.Coordinate.X.Should().Be(expected.Item4);
            geometry.Coordinate.Y.Should().Be(expected.Item5);
            geometry.Coordinate.Z.Should().Be(expected.Item6);
        }
    }
}
