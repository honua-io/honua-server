// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using Npgsql;
using Xunit;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Testcontainers integration coverage for the reconciled sink.external-postgis executor.
/// Proves the executor creates its destination table in a PostGIS database that is not the
/// Honua catalog and inserts features (tagged with the batch id) through the managed
/// Npgsql + WKB path — no GDAL. Uses an isolated schema as the "external" target.
/// Requires Docker; skipped automatically when the database fixture is unavailable.
/// </summary>
[Collection("Database")]
public sealed class ExternalPostgisSinkExecutorTests : IAsyncLifetime
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    private readonly DatabaseFixtureAdapter _fixture;
    private string _schemaName = null!;

    public ExternalPostgisSinkExecutorTests(DatabaseFixtureAdapter fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(ExternalPostgisSinkExecutorTests));
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesTableAndInsertsFeaturesWithBatchTag()
    {
        var executor = new ExternalPostgisSinkExecutor(Options());
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var input = BuildInputUri(
            new Feature(factory.CreatePoint(new Coordinate(13.405, 52.52)), new AttributesTable { { "name", "berlin" } }),
            new Feature(factory.CreatePoint(new Coordinate(-122.4, 37.6)), new AttributesTable { { "name", "sf" } }),
            new Feature(null, new AttributesTable { { "name", "no-geom" } }));

        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-ext");
        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var record = Record(
            ("input", input),
            ("connectionString", _fixture.ConnectionString),
            ("schema", _schemaName),
            ("table", "external_out"),
            ("targetSrid", "4326"),
            ("batchId", "batch-ext-1"));

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        Assert.True(
            result.Status == ExecutionJobStatus.Succeeded,
            result.ErrorMessage ?? "External PostGIS sink executor failed without an error message.");
        Assert.NotNull(publishedUri);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();

        await using var countCommand = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM \"{_schemaName}\".external_out", connection);
        Assert.Equal(2L, (long)(await countCommand.ExecuteScalarAsync())!);

        await using var batchCommand = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM \"{_schemaName}\".external_out " +
            "WHERE attributes->>'__pipeline_batch_id' = 'batch-ext-1'", connection);
        Assert.Equal(2L, (long)(await batchCommand.ExecuteScalarAsync())!);

        await using var sridCommand = new NpgsqlCommand(
            $"SELECT DISTINCT ST_SRID(geom) FROM \"{_schemaName}\".external_out", connection);
        Assert.Equal(4326, (int)(await sridCommand.ExecuteScalarAsync())!);
    }

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

    private static ExecutionJobRecord Record(params (string Name, string Value)[] inputs)
    {
        const string processId = ExternalPostgisSinkExecutor.HandledProcessId;
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

        return new ExecutionJobRecord
        {
            OperationId = "op-ext",
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
