// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
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
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver
            .ResolveConnectionStringAsync("external-target", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_fixture.ConnectionString));
        var executor = new ExternalPostgisSinkExecutor(Options(), resolver);
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
            ("connectionName", "external-target"),
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

    [Fact]
    public async Task ExecuteAsync_WithInlineConnectionString_FailsBeforeOpeningConnection()
    {
        var resolver = Substitute.For<ISecureConnectionResolver>();
        var executor = new ExternalPostgisSinkExecutor(Options(), resolver);
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var input = BuildInputUri(
            new Feature(factory.CreatePoint(new Coordinate(13.405, 52.52)), new AttributesTable { { "name", "berlin" } }));

        var result = await executor.ExecuteAsync(
            Record(
                ("input", input),
                ("connectionString", _fixture.ConnectionString),
                ("schema", _schemaName),
                ("table", "external_out"),
                ("targetSrid", "4326")),
            Substitute.For<IJobExecutionContext>(),
            CancellationToken.None);

        Assert.Equal(ExecutionJobStatus.Failed, result.Status);
        Assert.Contains("connectionString is not accepted", result.ErrorMessage, StringComparison.Ordinal);
        _ = resolver
            .DidNotReceiveWithAnyArgs()
            .ResolveConnectionStringAsync(default(string)!, default);
        _ = resolver
            .DidNotReceiveWithAnyArgs()
            .ResolveConnectionStringAsync(default(Guid), default);
    }

    [Fact]
    public async Task ExecuteAsync_MidBatchFailure_RollsBackAllPreviouslyInsertedRows()
    {
        // Regression for BH2-018: without the transaction wrapping, a constraint violation
        // on the 2nd batch would leave the 1st batch's rows permanently committed in the
        // external table. With the fix, the transaction wraps both batches
        // and the entire load is rolled back atomically.
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver
            .ResolveConnectionStringAsync("external-target", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_fixture.ConnectionString));
        var executor = new ExternalPostgisSinkExecutor(Options(), resolver);
        var factory = new GeometryFactory(new PrecisionModel(), 4326);

        // Manually create the target table with a CHECK constraint that rejects 'reject_me'.
        // CREATE TABLE IF NOT EXISTS in EnsureTableAsync will skip recreation because the table
        // already exists, so the constraint is preserved for the duration of this test.
        await using var setupConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var setupCmd = new NpgsqlCommand(
            $"""
            CREATE TABLE IF NOT EXISTS "{_schemaName}".partial_test (
                id         BIGSERIAL PRIMARY KEY,
                geom       geometry(Geometry, 4326),
                attributes JSONB NOT NULL,
                CONSTRAINT reject_me_constraint CHECK (attributes->>'name' != 'reject_me')
            );
            """,
            setupConnection);
        await setupCmd.ExecuteNonQueryAsync();

        var input = BuildInputUri(
            new Feature(factory.CreatePoint(new Coordinate(0, 0)), new AttributesTable { { "name", "first_feature" } }),
            new Feature(factory.CreatePoint(new Coordinate(1, 1)), new AttributesTable { { "name", "reject_me" } }));

        var record = Record(
            ("input", input),
            ("connectionName", "external-target"),
            ("schema", _schemaName),
            ("table", "partial_test"),
            ("targetSrid", "4326"),
            ("batchId", "batch-rollback-test"),
            ("batchSize", "1"));  // batchSize=1 so each feature is a separate batch

        var result = await executor.ExecuteAsync(record, Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        Assert.Equal(ExecutionJobStatus.Failed, result.Status);

        // Without the transaction fix, the first batch (first_feature) would be committed
        // before the second batch fails. With the fix, the transaction wraps both batches
        // and the entire load is rolled back → 0 rows in the table.
        await using var countConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var countCmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM \"{_schemaName}\".partial_test", countConnection);
        var count = (long)(await countCmd.ExecuteScalarAsync())!;
        Assert.Equal(0L, count);
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
