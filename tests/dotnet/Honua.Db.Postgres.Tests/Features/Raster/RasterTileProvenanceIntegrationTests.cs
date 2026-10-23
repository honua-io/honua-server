// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>Checks migration preservation and cache rejection after historical writer overwrites.</summary>
[Collection("Database")]
public sealed class RasterTileProvenanceIntegrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task MigrationAndAdmission_PreserveLegacyBytesAndRejectUnprovenOrOverwrittenTiles()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(RasterTileProvenanceIntegrationTests));
        try
        {
            await using var connection = await fixture.GetConnectionAsync(schema);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE raster_tiles (id integer PRIMARY KEY, tile_data bytea NOT NULL);
                INSERT INTO raster_tiles VALUES (1, decode('01020304', 'hex'));
                """;
            await command.ExecuteNonQueryAsync();

            var assembly = typeof(PostgresRasterStore).Assembly;
            var resource = assembly.GetManifestResourceNames().Single(name =>
                name.EndsWith("006_AddRasterTileRendererProvenance.sql", StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var migration = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
            command.CommandText = migration;
            await command.ExecuteNonQueryAsync();
            // The migration is repeatable and does not promote existing data to trusted.
            await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT encode(tile_data, 'hex'), renderer_version, renderer_payload_sha256 IS NULL FROM raster_tiles WHERE id = 1";
            await using (var legacy = await command.ExecuteReaderAsync())
            {
                (await legacy.ReadAsync()).Should().BeTrue();
                legacy.GetString(0).Should().Be("01020304");
                legacy.GetInt32(1).Should().Be(0);
                legacy.GetBoolean(2).Should().BeTrue();
            }

            command.Parameters.AddWithValue("rendererVersion", RasterCorrectedNearestSql.RendererVersion);
            command.CommandText = """
                INSERT INTO raster_tiles (id, tile_data, renderer_version, renderer_payload_sha256) VALUES
                    (2, decode('05060708', 'hex'), @rendererVersion, sha256(decode('05060708', 'hex'))),
                    (3, decode('05060708', 'hex'), @rendererVersion, NULL),
                    (4, decode('05060708', 'hex'), @rendererVersion, decode('0000', 'hex')),
                    (5, decode('05060708', 'hex'), @rendererVersion, sha256(decode('01020304', 'hex'))),
                    (6, decode('05060708', 'hex'), @rendererVersion + 1, sha256(decode('05060708', 'hex')));
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"SELECT array_agg(id ORDER BY id) FROM raster_tiles WHERE {RasterTileProvenanceSql.AdmissionPredicate}";
            ((int[])(await command.ExecuteScalarAsync())!).Should().Equal(2);

            // This is the old importer's ON CONFLICT shape: it changes only payload bytes.
            command.CommandText = """
                INSERT INTO raster_tiles (id, tile_data) VALUES (2, decode('aabbccdd', 'hex'))
                ON CONFLICT (id) DO UPDATE SET tile_data = EXCLUDED.tile_data;
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"SELECT count(*) FROM raster_tiles WHERE {RasterTileProvenanceSql.AdmissionPredicate}";
            ((long)(await command.ExecuteScalarAsync())!).Should().Be(0);
            command.CommandText = "SELECT renderer_version, encode(tile_data, 'hex') FROM raster_tiles WHERE id = 2";
            await using (var overwritten = await command.ExecuteReaderAsync())
            {
                (await overwritten.ReadAsync()).Should().BeTrue();
                overwritten.GetInt32(0).Should().Be(RasterCorrectedNearestSql.RendererVersion);
                overwritten.GetString(1).Should().Be("aabbccdd");
            }

            // The updated legacy renderer records its actual version and byte digest.
            // Even a valid body hash cannot promote that renderer to the corrected version.
            command.Parameters.AddWithValue("legacyVersion", RasterTileProvenanceSql.LegacyRendererVersion);
            command.CommandText = """
                INSERT INTO raster_tiles (id, tile_data, renderer_version, renderer_payload_sha256)
                VALUES (2, decode('11223344', 'hex'), @legacyVersion, sha256(decode('11223344', 'hex')))
                ON CONFLICT (id) DO UPDATE SET
                    tile_data = EXCLUDED.tile_data,
                    renderer_version = EXCLUDED.renderer_version,
                    renderer_payload_sha256 = EXCLUDED.renderer_payload_sha256;
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"""
                SELECT renderer_version, renderer_payload_sha256 = sha256(tile_data),
                       {RasterTileProvenanceSql.AdmissionPredicate}
                FROM raster_tiles WHERE id = 2;
                """;
            await using var legacyRewrite = await command.ExecuteReaderAsync();
            (await legacyRewrite.ReadAsync()).Should().BeTrue();
            legacyRewrite.GetInt32(0).Should().Be(RasterTileProvenanceSql.LegacyRendererVersion);
            legacyRewrite.GetBoolean(1).Should().BeTrue();
            legacyRewrite.GetBoolean(2).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
