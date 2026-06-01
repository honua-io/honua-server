// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Seed;

/// <summary>
/// Regression tests for the Metadata v2 compat-compiler
/// (<c>honua.seed_metadata_v2_compat_snapshot()</c>) that exercise the SQL seed
/// directly against a fresh PostGIS container and assert the compiled snapshot.
///
/// Covers honua-server#1345 (access policy must be carried through so a protected
/// service stays protected after compile) and honua-server#1312 (shared-features
/// storage bindings must carry layerDiscriminatorColumn/geometryColumn/attributesColumn
/// so reads are constrained to a single layer and project geometry).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class CompatCompileBindingAndAccessPolicyTests
{
    private const string PostgisImage = "postgis/postgis:16-3.4";
    private const string TestRunIdEnv = "HONUA_TEST_RUN_ID";

    // Two shared-`features` layers bound to a single non-anonymous service.
    private const int ProtectedPointLayerId = 4100;
    private const int ProtectedLineLayerId = 4101;

    private static readonly string[] _seedSql =
    [
        // A PROTECTED service (accessPolicy.allowAnonymous=false). After compile the
        // compiled service AND its layer resources must still be non-anonymous.
        """
        INSERT INTO honua.services (
            service_name, description, srid, max_record_count,
            supported_formats, capabilities, service_extent, metadata
        )
        VALUES (
            'compat_protected', 'Compat protected service', 4326, 1000,
            ARRAY['JSON', 'GeoJSON'],
            ARRAY['Query'],
            ST_MakeEnvelope(-122.5, 37.7, -122.35, 37.84, 4326),
            jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false))
        )
        ON CONFLICT (service_name) DO UPDATE SET metadata = EXCLUDED.metadata;
        """,
        """
        INSERT INTO honua.layers (
            layer_id, layer_name, description, table_name,
            geometry_type, srid, extent, default_visibility, metadata
        )
        VALUES
            (4100, 'Protected Points', 'shared features point layer',
             'features', 'Point', 4326,
             ST_MakeEnvelope(-122.5, 37.7, -122.35, 37.84, 4326), true,
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false))),
            (4101, 'Protected Lines', 'shared features line layer',
             'features', 'LineString', 4326,
             ST_MakeEnvelope(-122.5, 37.7, -122.35, 37.84, 4326), true,
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false)))
        ON CONFLICT (layer_id) DO UPDATE SET metadata = EXCLUDED.metadata;
        """,
        """
        INSERT INTO honua.layer_fields (layer_id, field_name, field_type, field_order, nullable, description)
        VALUES
            (4100, 'objectid', 'Integer', 0, false, 'Object ID'),
            (4100, 'name',     'String',  1, true,  'Name'),
            (4100, 'shape',    'Geometry', 2, true,  'Geometry'),
            (4101, 'objectid', 'Integer', 0, false, 'Object ID'),
            (4101, 'name',     'String',  1, true,  'Name'),
            (4101, 'shape',    'Geometry', 2, true,  'Geometry')
        ON CONFLICT (layer_id, field_name) DO NOTHING;
        """,
        """
        INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
        VALUES ('compat_protected', 4100, 0), ('compat_protected', 4101, 1)
        ON CONFLICT (service_name, layer_id) DO NOTHING;
        """,
        // Three rows for layer 4100, two for layer 4101 — distinct counts so a
        // discriminator regression (returning all rows) is detectable.
        """
        INSERT INTO features (layer_id, geometry, attributes)
        SELECT 4100, ST_SetSRID(ST_GeomFromText(wkt), 4326), jsonb_build_object('name', name)
        FROM (VALUES
            ('p-a', 'POINT(-122.4194 37.7749)'),
            ('p-b', 'POINT(-122.4180 37.7760)'),
            ('p-c', 'POINT(-122.4210 37.7735)')
        ) AS seed(name, wkt);
        """,
        """
        INSERT INTO features (layer_id, geometry, attributes)
        SELECT 4101, ST_SetSRID(ST_GeomFromText(wkt), 4326), jsonb_build_object('name', name)
        FROM (VALUES
            ('l-a', 'LINESTRING(-122.43 37.77, -122.41 37.78)'),
            ('l-b', 'LINESTRING(-122.42 37.775, -122.40 37.785)')
        ) AS seed(name, wkt);
        """,
        "SELECT honua.seed_metadata_v2_compat_snapshot();",
    ];

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task CompatCompile_ProtectedSharedFeaturesService_CarriesAccessPolicyAndBindingColumns()
    {
        await using var container = new PostgreSqlBuilder()
            .WithImage(PostgisImage)
            .WithDatabase("honua_compat_compile_regression")
            .WithUsername("postgres")
            .WithPassword("compat_password")
            .WithEnvironment("POSTGIS_GDAL_ENABLED_DRIVERS", "ENABLE_ALL")
            .WithLabel("honua.test.owner", "honua-server")
            .WithLabel("honua.test.run_id", Environment.GetEnvironmentVariable(TestRunIdEnv) ?? "manual")
            .Build();

        await container.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Timeout = 60,
            CommandTimeout = 120
        }.ToString();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);

        await ExecuteSqlAsync(dataSource, await File.ReadAllTextAsync(RepositoryPaths.Resolve("tests", "seed", "client-compat-v1.sql")));
        foreach (var sql in _seedSql)
        {
            await ExecuteSqlAsync(dataSource, sql);
        }

        var snapshot = await ReadDefaultSnapshotAsync(dataSource);
        using var document = JsonDocument.Parse(snapshot);
        var root = document.RootElement;

        // honua-server#1345 — the compiled service for a non-anonymous service must
        // NOT be anonymous. A regression (drop to allowAnonymous=true) would let an
        // unauthenticated client read a protected service.
        var protectedService = root.GetProperty("services")
            .EnumerateArray()
            .Single(service =>
                service.GetProperty("metadata").GetProperty("name").GetString() == "compat_protected" &&
                service.GetProperty("serviceType").GetString() == "esri-feature-service");
        protectedService.GetProperty("accessPolicy").GetProperty("allowAnonymous").GetBoolean()
            .Should().BeFalse("a service declared non-anonymous must stay protected after compile (#1345)");

        // The layer resources for the protected layers must also carry the policy.
        var protectedResources = root.GetProperty("resources")
            .EnumerateArray()
            .Where(resource =>
            {
                var id = resource.GetProperty("metadata").GetProperty("id").GetString();
                return id is "res-layer-4100" or "res-layer-4101";
            })
            .ToArray();
        protectedResources.Should().HaveCount(2);
        foreach (var resource in protectedResources)
        {
            resource.GetProperty("accessPolicy").GetProperty("allowAnonymous").GetBoolean()
                .Should().BeFalse("protected layer resources must stay non-anonymous after compile (#1345)");
        }

        // honua-server#1312 — shared-`features` storage bindings must carry the
        // discriminator + geometry + attributes columns so reads are constrained
        // to a single layer and geometry resolves.
        var sharedBindings = root.GetProperty("storageBindings")
            .EnumerateArray()
            .Where(binding =>
            {
                var id = binding.GetProperty("metadata").GetProperty("id").GetString();
                return id is "storage-layer-4100" or "storage-layer-4101";
            })
            .ToArray();
        sharedBindings.Should().HaveCount(2);
        foreach (var binding in sharedBindings)
        {
            var options = binding.GetProperty("options");
            options.GetProperty("layerDiscriminatorColumn").GetString().Should().Be("layer_id");
            options.GetProperty("geometryColumn").GetString().Should().Be("geometry");
            options.GetProperty("attributesColumn").GetString().Should().Be("attributes");
        }

        // The bound base-schema layer 0 also lives on the shared `features` table
        // and must remain anonymous (no policy seeded => default open).
        var baseService = root.GetProperty("services")
            .EnumerateArray()
            .Single(service =>
                service.GetProperty("metadata").GetProperty("name").GetString() == "test_service" &&
                service.GetProperty("serviceType").GetString() == "esri-feature-service");
        baseService.GetProperty("accessPolicy").GetProperty("allowAnonymous").GetBoolean()
            .Should().BeTrue("a service without a seeded policy defaults to anonymous");
    }

    private static async Task ExecuteSqlAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadDefaultSnapshotAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.document::text
            FROM honua.metadata_v2_current c
            JOIN honua.metadata_v2_snapshots s
              ON s.environment = c.environment
             AND s.revision = c.revision
            WHERE c.environment = 'default';
            """;
        var result = await command.ExecuteScalarAsync();
        return (string)result!;
    }
}
