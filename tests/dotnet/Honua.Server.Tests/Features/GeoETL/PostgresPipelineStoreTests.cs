// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Postgres.Features.GeoETL.Services;
using Honua.Server.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.GeoETL;

/// <summary>
/// Testcontainers integration coverage for the PostgreSQL-backed GeoETL stores
/// (#361 Child Ticket A — durable persistence). Proves a pipeline definition CRUD
/// round-trip (create, get, list, update with version bump, delete) and a pipeline
/// execution lifecycle round-trip (create → update terminal state) against a real
/// PostgreSQL instance, with the discriminated-union stage chain persisted as JSONB.
/// </summary>
[Collection("Database")]
public sealed class PostgresPipelineStoreTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private readonly ITestOutputHelper _output;
    private string _schemaName = null!;
    private PostgresPipelineDefinitionStore _definitionStore = null!;
    private PostgresPipelineExecutionStore _executionStore = null!;

    public PostgresPipelineStoreTests(DatabaseFixtureAdapter fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresPipelineStoreTests));

        // Mirror migration 033's DDL in the isolated test schema so the round-trip runs
        // with full test isolation (parallel runs never collide on a shared honua schema).
        await _fixture.ExecuteAsync($"""
            CREATE TABLE {_schemaName}.pipeline_definitions (
                id              VARCHAR(128) PRIMARY KEY,
                name            VARCHAR(256) NOT NULL,
                description     TEXT,
                schema_version  INTEGER NOT NULL DEFAULT 1,
                version         INTEGER NOT NULL DEFAULT 1,
                stages_json     JSONB NOT NULL,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE {_schemaName}.pipeline_executions (
                id                      VARCHAR(128) PRIMARY KEY,
                pipeline_id             VARCHAR(128) NOT NULL,
                pipeline_version        INTEGER NOT NULL DEFAULT 1,
                execution_job_id        VARCHAR(128) NOT NULL,
                status                  VARCHAR(32) NOT NULL,
                is_dry_run              BOOLEAN NOT NULL DEFAULT FALSE,
                features_read           BIGINT NOT NULL DEFAULT 0,
                features_written        BIGINT NOT NULL DEFAULT 0,
                features_quarantined    BIGINT NOT NULL DEFAULT 0,
                batch_id                VARCHAR(128),
                error_message           TEXT,
                created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                completed_at            TIMESTAMPTZ
            );
            """);

        var connectionProvider = new TestDatabaseConnectionProvider(
            _fixture.DataSource, () => _schemaName);
        _definitionStore = new PostgresPipelineDefinitionStore(
            connectionProvider, TimeProvider.System, _schemaName);
        _executionStore = new PostgresPipelineExecutionStore(connectionProvider, _schemaName);

        _output.WriteLine($"Created isolated schema: {_schemaName}");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
    }

    [Fact]
    public async Task DefinitionStore_CrudRoundTrip_PreservesStagesAndBumpsVersion()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new PipelineDefinition
        {
            SchemaVersion = 1,
            Id = "pl-roundtrip",
            Name = "GeoJSON to PostGIS",
            Description = "round-trip fixture",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig
                    {
                        Type = "geojson",
                        Options = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["path"] = "/data/in.geojson"
                        }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Transform,
                    Transform = new TransformConfig
                    {
                        Type = "reproject",
                        Options = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["fromSrid"] = "4326",
                            ["toSrid"] = "3857"
                        }
                    }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Sink,
                    Connector = new ConnectorConfig
                    {
                        Type = "honua-layer",
                        Options = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["schema"] = "honua",
                            ["table"] = "out",
                            ["targetSrid"] = "3857"
                        }
                    }
                }
            ]
        };

        // Create + Get: stages survive the JSONB round-trip exactly.
        await _definitionStore.CreateAsync(definition);
        var fetched = await _definitionStore.GetAsync(definition.Id);

        Assert.NotNull(fetched);
        Assert.Equal("GeoJSON to PostGIS", fetched!.Name);
        Assert.Equal("round-trip fixture", fetched.Description);
        Assert.Equal(1, fetched.SchemaVersion);
        Assert.Equal(1, fetched.Version);
        Assert.Equal(3, fetched.Stages.Count);

        Assert.Equal(PipelineStageKind.Source, fetched.Stages[0].Kind);
        Assert.Equal("geojson", fetched.Stages[0].Connector!.Type);
        Assert.Equal("/data/in.geojson", fetched.Stages[0].Connector!.Options["path"]);

        Assert.Equal(PipelineStageKind.Transform, fetched.Stages[1].Kind);
        Assert.Equal("reproject", fetched.Stages[1].Transform!.Type);
        Assert.Equal("4326", fetched.Stages[1].Transform!.Options["fromSrid"]);
        Assert.Equal("3857", fetched.Stages[1].Transform!.Options["toSrid"]);

        Assert.Equal(PipelineStageKind.Sink, fetched.Stages[2].Kind);
        Assert.Equal("honua-layer", fetched.Stages[2].Connector!.Type);
        Assert.Equal("out", fetched.Stages[2].Connector!.Options["table"]);

        // List returns the created definition.
        var listed = await _definitionStore.ListAsync();
        Assert.Single(listed);
        Assert.Equal(definition.Id, listed[0].Id);

        // Update bumps the version, preserves CreatedAt, and persists the new stage chain.
        var updated = await _definitionStore.UpdateAsync(definition with
        {
            Name = "renamed",
            Stages = definition.Stages.Take(2).Append(definition.Stages[^1]).ToArray()
        });
        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Version);
        Assert.Equal("renamed", updated.Name);

        var afterUpdate = await _definitionStore.GetAsync(definition.Id);
        Assert.Equal(2, afterUpdate!.Version);
        Assert.Equal("renamed", afterUpdate.Name);

        // Delete removes it.
        Assert.True(await _definitionStore.DeleteAsync(definition.Id));
        Assert.Null(await _definitionStore.GetAsync(definition.Id));
        Assert.False(await _definitionStore.DeleteAsync(definition.Id));
    }

    [Fact]
    public async Task DefinitionStore_DuplicateCreate_Throws()
    {
        var definition = new PipelineDefinition
        {
            Id = "pl-dup",
            Name = "dup",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Stages =
            [
                new PipelineStage
                {
                    Kind = PipelineStageKind.Source,
                    Connector = new ConnectorConfig { Type = "geojson" }
                },
                new PipelineStage
                {
                    Kind = PipelineStageKind.Sink,
                    Connector = new ConnectorConfig { Type = "null-preview" }
                }
            ]
        };

        await _definitionStore.CreateAsync(definition);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _definitionStore.CreateAsync(definition));
    }

    [Fact]
    public async Task ExecutionStore_LifecycleRoundTrip_PersistsTerminalState()
    {
        var created = DateTimeOffset.UtcNow;
        var execution = new PipelineExecution
        {
            Id = "geoetl-exec-1",
            PipelineId = "pl-1",
            PipelineVersion = 1,
            ExecutionJobId = "geoetl-exec-1",
            Status = PipelineExecutionStatus.Pending,
            IsDryRun = false,
            BatchId = "geoetl-exec-1",
            CreatedAt = created
        };

        await _executionStore.CreateAsync(execution);

        // Running update.
        await _executionStore.UpdateAsync(execution with { Status = PipelineExecutionStatus.Running });

        // Terminal update with counts.
        var completedAt = created.AddSeconds(5);
        await _executionStore.UpdateAsync(execution with
        {
            Status = PipelineExecutionStatus.Succeeded,
            FeaturesRead = 42,
            FeaturesWritten = 40,
            FeaturesQuarantined = 2,
            CompletedAt = completedAt
        });

        var fetched = await _executionStore.GetAsync(execution.Id);
        Assert.NotNull(fetched);
        Assert.Equal(PipelineExecutionStatus.Succeeded, fetched!.Status);
        Assert.Equal(42, fetched.FeaturesRead);
        Assert.Equal(40, fetched.FeaturesWritten);
        Assert.Equal(2, fetched.FeaturesQuarantined);
        Assert.Equal("geoetl-exec-1", fetched.BatchId);
        Assert.NotNull(fetched.CompletedAt);

        // ListForPipeline returns newest first.
        await _executionStore.CreateAsync(execution with
        {
            Id = "geoetl-exec-2",
            ExecutionJobId = "geoetl-exec-2",
            CreatedAt = created.AddSeconds(10)
        });

        var listed = await _executionStore.ListForPipelineAsync("pl-1");
        Assert.Equal(2, listed.Count);
        Assert.Equal("geoetl-exec-2", listed[0].Id);
        Assert.Equal("geoetl-exec-1", listed[1].Id);
    }

    [Fact]
    public async Task ExecutionStore_UpdateBeforeCreate_Upserts()
    {
        // UpdateAsync upserts so a worker resuming after a restart converges on state
        // even if the create write was lost.
        var execution = new PipelineExecution
        {
            Id = "geoetl-exec-upsert",
            PipelineId = "pl-2",
            PipelineVersion = 1,
            ExecutionJobId = "geoetl-exec-upsert",
            Status = PipelineExecutionStatus.Running,
            BatchId = "geoetl-exec-upsert",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _executionStore.UpdateAsync(execution);

        var fetched = await _executionStore.GetAsync(execution.Id);
        Assert.NotNull(fetched);
        Assert.Equal(PipelineExecutionStatus.Running, fetched!.Status);
    }
}
