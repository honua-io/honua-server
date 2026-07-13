// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

/// <summary>
/// Integration tests for the admin raster CRUD gap (#1875) and the sensor-metadata hydration
/// foundation (#1879/#1880/#1881) on <see cref="PostgresRasterStore"/>.
/// </summary>
[Collection("Database")]
public sealed class PostgresRasterStoreCrudTests(PostgresFixture fixture)
{
    private const int LayerId = 9050;

    [IntegrationTest]
    public async Task DeleteRasterAsync_RemovesRowAndReturnsTrue()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCrudTests));
        try
        {
            await CreateRasterSchemaAsync(schemaName);
            var rasterId = await InsertRasterAsync(schemaName, "to-delete");
            var store = CreateStore(schemaName);

            var deleted = await store.DeleteRasterAsync(LayerId, rasterId);
            deleted.Should().BeTrue();

            (await store.GetRasterInfoAsync(LayerId, rasterId)).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task DeleteRasterAsync_MissingRaster_ReturnsFalse()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCrudTests));
        try
        {
            await CreateRasterSchemaAsync(schemaName);
            var store = CreateStore(schemaName);

            (await store.DeleteRasterAsync(LayerId, 987654)).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task DeleteRasterAsync_CascadesSensorMetadata()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCrudTests));
        try
        {
            await CreateRasterSchemaAsync(schemaName);
            var rasterId = await InsertRasterAsync(schemaName, "with-sensor");
            await InsertSensorMetadataAsync(schemaName, rasterId, "WorldView-3", offNadir: 9.0, demSource: "42");
            var store = CreateStore(schemaName);

            await store.DeleteRasterAsync(LayerId, rasterId);

            var remaining = await store.GetSensorMetadataAsync([rasterId]);
            remaining.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task UpdateRasterMetadataAsync_UpdatesNameAndAcquisitionDate()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCrudTests));
        try
        {
            await CreateRasterSchemaAsync(schemaName);
            var rasterId = await InsertRasterAsync(schemaName, "original");
            var store = CreateStore(schemaName);

            var newDate = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var update = new RasterMetadataUpdate
            {
                Name = "renamed",
                AcquisitionDate = Optional.Of<DateTimeOffset?>(newDate),
            };

            var updated = await store.UpdateRasterMetadataAsync(LayerId, rasterId, update);
            updated.Should().NotBeNull();
            var updatedMetadata = updated is { } m ? m : throw new InvalidOperationException("Expected updated raster metadata.");
            updatedMetadata.Name.Should().Be("renamed");
            updatedMetadata.AcquisitionDate!.Value.UtcDateTime.Should().Be(newDate.UtcDateTime);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task UpdateRasterMetadataAsync_MissingRaster_ReturnsNull()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCrudTests));
        try
        {
            await CreateRasterSchemaAsync(schemaName);
            var store = CreateStore(schemaName);

            var update = new RasterMetadataUpdate { Name = "x" };
            (await store.UpdateRasterMetadataAsync(LayerId, 55555, update)).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetRasterLayerIdAsync_ResolvesOwningLayer()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCrudTests));
        try
        {
            await CreateRasterSchemaAsync(schemaName);
            var rasterId = await InsertRasterAsync(schemaName, "owned");
            var store = CreateStore(schemaName);

            (await store.GetRasterLayerIdAsync(rasterId)).Should().Be(LayerId);
            (await store.GetRasterLayerIdAsync(424242)).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetSensorMetadataAsync_HydratesModeledFields()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCrudTests));
        try
        {
            await CreateRasterSchemaAsync(schemaName);
            var rasterId = await InsertRasterAsync(schemaName, "sensor");
            await InsertSensorMetadataAsync(schemaName, rasterId, "Pleiades", offNadir: 14.0, demSource: "77");
            var store = CreateStore(schemaName);

            var metadata = await store.GetSensorMetadataAsync([rasterId]);
            metadata.Should().ContainKey(rasterId);
            var sensor = metadata[rasterId];
            sensor.SensorName.Should().Be("Pleiades");
            sensor.DemSource.Should().Be("77");
            sensor.ExteriorOrientationJson.Should().Contain("14");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetSensorMetadataAsync_EmptyInput_ReturnsEmpty()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreCrudTests));
        try
        {
            await CreateRasterSchemaAsync(schemaName);
            var store = CreateStore(schemaName);

            (await store.GetSensorMetadataAsync(Array.Empty<long>())).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private PostgresRasterStore CreateStore(string schemaName)
        => new(new FixtureConnectionProvider(fixture.DataSource), NullLogger<PostgresRasterStore>.Instance, schemaName);

    private async Task CreateRasterSchemaAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                name VARCHAR(255) NOT NULL,
                description TEXT,
                raster raster NOT NULL,
                acquisition_date TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ,
                width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
                height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
                band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
                pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
                srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
            );
            CREATE TABLE IF NOT EXISTS raster_sensor_metadata (
                raster_data_id BIGINT PRIMARY KEY REFERENCES raster_data(id) ON DELETE CASCADE,
                sensor_name VARCHAR(255),
                camera_model VARCHAR(255),
                interior_orientation JSONB,
                exterior_orientation JSONB,
                rpc JSONB,
                dem_source VARCHAR(512),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertRasterAsync(string schemaName, string name)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, created_at)
            SELECT @layerId, @name,
                   ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326), '8BUI'::text, 7, NULL),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task InsertSensorMetadataAsync(
        string schemaName, long rasterId, string sensorName, double offNadir, string demSource)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_sensor_metadata (raster_data_id, sensor_name, exterior_orientation, dem_source)
            VALUES (@id, @name, jsonb_build_object('offNadirAngle', @angle), @dem);
            """;
        command.Parameters.AddWithValue("id", rasterId);
        command.Parameters.AddWithValue("name", sensorName);
        command.Parameters.AddWithValue("angle", offNadir);
        command.Parameters.AddWithValue("dem", demSource);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixtureConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }
}
