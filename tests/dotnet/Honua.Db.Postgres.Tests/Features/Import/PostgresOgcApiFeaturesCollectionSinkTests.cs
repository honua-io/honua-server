// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Integration tests for the OGC API Features collection sink. Exercises real PostgreSQL/PostGIS
/// via the shared Testcontainers fixture so the SQL/PostGIS contract is enforced.
/// </summary>
[Collection("Database")]
public sealed class PostgresOgcApiFeaturesCollectionSinkTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private string? _schema;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _schema = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresOgcApiFeaturesCollectionSinkTests));
    }

    public async Task DisposeAsync()
    {
        if (_schema != null)
        {
            await _fixture.DropSchemaAsync(_schema);
        }

        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task EnsureTargetAsync_CreatesTargetTableWithExpectedShape()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_target",
            CollectionId = "roads"
        };

        await sink.EnsureTargetAsync(target, CancellationToken.None);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        // Postgres truncates identifiers to NAMEDATALEN-1 (63 bytes by default), so we look up by
        // the truncated form rather than relying on the fixture-provided schema name verbatim.
        var truncatedSchema = _schema!.Length > 63 ? _schema![..63] : _schema!;
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table ORDER BY ordinal_position",
            connection);
        command.Parameters.AddWithValue("@schema", truncatedSchema);
        command.Parameters.AddWithValue("@table", "roads_target");

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        columns.Should().BeEquivalentTo("source_feature_id", "properties", "geometry", "imported_at");
    }

    [Fact]
    public async Task WriteFeaturesAsync_InsertsRowsAndIsIdempotentOnReRun()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_target",
            CollectionId = "roads"
        };
        await sink.EnsureTargetAsync(target, CancellationToken.None);

        var batch = new List<OgcApiFeaturesSinkFeature>
        {
            new()
            {
                SourceFeatureId = "road.1",
                GeoJsonGeometry = "{\"type\":\"Point\",\"coordinates\":[-157.85,21.30]}",
                PropertiesJson = "{\"name\":\"King\"}"
            },
            new()
            {
                SourceFeatureId = "road.2",
                GeoJsonGeometry = "{\"type\":\"Point\",\"coordinates\":[-157.86,21.31]}",
                PropertiesJson = "{\"name\":\"Beretania\"}"
            }
        };

        var first = await sink.WriteFeaturesAsync(target, batch, CancellationToken.None);
        var firstCount = await CountRowsAsync(target);

        // Re-run with a mutated property to verify upsert semantics.
        var second = await sink.WriteFeaturesAsync(target, new List<OgcApiFeaturesSinkFeature>
        {
            batch[0] with { PropertiesJson = "{\"name\":\"King Updated\"}" },
            batch[1]
        }, CancellationToken.None);
        var secondCount = await CountRowsAsync(target);

        first.Should().Be(2);
        firstCount.Should().Be(2);
        second.Should().Be(2);
        secondCount.Should().Be(2);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT properties->>'name' FROM \"{_schema}\".\"roads_target\" WHERE source_feature_id = 'road.1'",
            connection);
        var name = (string?)await command.ExecuteScalarAsync();
        name.Should().Be("King Updated");
    }

    [Fact]
    public async Task WriteFeaturesAsync_StoresGeometryAsPostgisGeometry()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_geom",
            CollectionId = "roads"
        };
        await sink.EnsureTargetAsync(target, CancellationToken.None);

        await sink.WriteFeaturesAsync(target, new List<OgcApiFeaturesSinkFeature>
        {
            new()
            {
                SourceFeatureId = "road.geom",
                GeoJsonGeometry = "{\"type\":\"Point\",\"coordinates\":[-157.85,21.30]}",
                PropertiesJson = "{}"
            }
        }, CancellationToken.None);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT ST_SRID(geometry), ST_AsText(geometry) FROM \"{_schema}\".\"roads_geom\" WHERE source_feature_id = 'road.geom'",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(4326);
        reader.GetString(1).Should().Contain("POINT");
    }

    [Fact]
    public async Task WriteFeaturesAsync_AcceptsNullGeometry()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_null",
            CollectionId = "roads"
        };
        await sink.EnsureTargetAsync(target, CancellationToken.None);

        var written = await sink.WriteFeaturesAsync(target, new List<OgcApiFeaturesSinkFeature>
        {
            new()
            {
                SourceFeatureId = "missing.geom",
                GeoJsonGeometry = null,
                PropertiesJson = "{\"flag\":true}"
            }
        }, CancellationToken.None);

        written.Should().Be(1);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT geometry IS NULL FROM \"{_schema}\".\"roads_null\" WHERE source_feature_id = 'missing.geom'",
            connection);
        var isNull = (bool?)await command.ExecuteScalarAsync();
        isNull.Should().BeTrue();
    }

    [Fact]
    public async Task GetLastScopeSignatureAsync_ReturnsRecordedSignatureAcrossRuns()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "roads_scope",
            CollectionId = "roads"
        };

        await sink.EnsureTargetAsync(target, CancellationToken.None);

        var initial = await sink.GetLastScopeSignatureAsync(target, CancellationToken.None);
        await sink.RecordScopeSignatureAsync(target, "f=name='alpha'|b=|d=", CancellationToken.None);
        var afterFirstWrite = await sink.GetLastScopeSignatureAsync(target, CancellationToken.None);
        await sink.RecordScopeSignatureAsync(target, "f=name='beta'|b=|d=", CancellationToken.None);
        var afterSecondWrite = await sink.GetLastScopeSignatureAsync(target, CancellationToken.None);

        initial.Should().BeNull();
        afterFirstWrite.Should().Be("f=name='alpha'|b=|d=");
        afterSecondWrite.Should().Be("f=name='beta'|b=|d=");
    }

    [Fact]
    public async Task EnsureTargetAsync_RejectsIdentifiersWithUnsafeCharacters()
    {
        var sink = CreateSink();
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = _schema!,
            Table = "evil; DROP TABLE pg_class",
            CollectionId = "roads"
        };

        await FluentActions
            .Awaiting(() => sink.EnsureTargetAsync(target, CancellationToken.None))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EnsureTargetAsync_CaseVariantOfOperationalSchema_UsesConfiguredSpelling()
    {
        // Quoted identifiers keep case. A request spelling that only differs by case from a
        // configured operational schema must write to that schema, not create a second one.
        const string configuredSchema = "OpSchemaGis5379";
        const string requestedSchema = "opschemagis5379";
        const string table = "case_variant_roads";

        var sink = new PostgresOgcApiFeaturesCollectionSink(
            _fixture.DataSource,
            NullLogger<PostgresOgcApiFeaturesCollectionSink>.Instance,
            new PostgresSchemaConfiguration(
                PostgresSchemaConfiguration.DefaultMetadataSchema,
                PostgresSchemaConfiguration.DefaultDataSchema,
                [PostgresSchemaConfiguration.DefaultDataSchema, "public", configuredSchema]));
        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = requestedSchema,
            Table = table,
            CollectionId = "roads"
        };

        try
        {
            await sink.EnsureTargetAsync(target, CancellationToken.None);
            var written = await sink.WriteFeaturesAsync(
                target,
                [
                    new OgcApiFeaturesSinkFeature
                    {
                        SourceFeatureId = "road.1",
                        GeoJsonGeometry = "{\"type\":\"Point\",\"coordinates\":[-157.85,21.30]}",
                        PropertiesJson = "{\"name\":\"King\"}"
                    }
                ],
                CancellationToken.None);
            await sink.RecordScopeSignatureAsync(target, "scope-canonical", CancellationToken.None);
            var scope = await sink.GetLastScopeSignatureAsync(target, CancellationToken.None);
            var columns = await sink.GetTargetColumnsAsync(target, CancellationToken.None);

            written.Should().Be(1);
            scope.Should().Be("scope-canonical");
            columns.Select(column => column.Name).Should().Contain("source_feature_id");

            var schemas = await SchemaNamesHoldingTableAsync(table);
            schemas.Should().ContainSingle().Which.Should().Be(configuredSchema);
            (await NamespaceExistsAsync(requestedSchema)).Should().BeFalse();

            var indexSchema = await IndexSchemaAsync(table + "_geometry_gix");
            indexSchema.Should().Be(configuredSchema);
        }
        finally
        {
            await _fixture.ExecuteDdlUnderLockAsync(
                $"""DROP SCHEMA IF EXISTS "{configuredSchema}" CASCADE; DROP SCHEMA IF EXISTS "{requestedSchema}" CASCADE;""");
        }
    }

    private async Task<IReadOnlyList<string>> SchemaNamesHoldingTableAsync(string table)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind = 'r'
               AND c.relname = @table
             ORDER BY n.nspname
            """,
            connection);
        command.Parameters.AddWithValue("@table", table);

        var schemas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas;
    }

    private async Task<bool> NamespaceExistsAsync(string schema)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)",
            connection);
        command.Parameters.AddWithValue("@schema", schema);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> IndexSchemaAsync(string indexName)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind = 'i'
               AND c.relname = @index
            """,
            connection);
        command.Parameters.AddWithValue("@index", indexName);
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    private PostgresOgcApiFeaturesCollectionSink CreateSink()
        => new(
            _fixture.DataSource,
            NullLogger<PostgresOgcApiFeaturesCollectionSink>.Instance,
            ImportTestSchemaConfiguration.WithOperational(_schema));

    private async Task<int> CountRowsAsync(OgcApiFeaturesSinkTarget target)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM \"{target.Schema}\".\"{target.Table}\"",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
