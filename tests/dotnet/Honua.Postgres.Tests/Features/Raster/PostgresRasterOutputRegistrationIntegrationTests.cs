// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgresRasterOutputRegistrationIntegrationTests(PostgresFixture fixture)
{
    private const int LayerId = 522;

    [IntegrationTest]
    public async Task RegisterPostgisAsync_CommitsRasterDerivedDataAndVisibilityAtomically()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(
            nameof(PostgresRasterOutputRegistrationIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            var bytes = await CreateGeoTiffAsync(width: 64, height: 64, pixelType: "8BUI");
            var descriptor = CreateDescriptor(
                bytes.LongLength,
                Sha256(bytes),
                width: 64,
                height: 64,
                dataType: "Byte",
                noDataValue: "0");
            var objectStore = Substitute.For<IRasterOutputObjectStore>();
            objectStore.OpenReadAsync(
                    descriptor.StoreReference,
                    descriptor.ObjectKey,
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false)));
            var registry = CreateRegistry(schemaName, objectStore);
            var command = CreateCommand(descriptor, "rast-import-success");

            var first = await registry.RegisterAtomicallyAsync(command);
            var replay = await registry.RegisterAtomicallyAsync(command);

            var output = Assert.IsType<PostgisRasterOutputDescriptor>(first.Output);
            Assert.False(first.AlreadyRegistered);
            Assert.True(replay.AlreadyRegistered);
            Assert.Equal(output.RasterId, Assert.IsType<PostgisRasterOutputDescriptor>(replay.Output).RasterId);
            Assert.Equal(1, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_data;"));
            Assert.Equal(1, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_statistics;"));
            Assert.Equal(4, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_overviews;"));
            Assert.Equal(1, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_footprints;"));
            Assert.Equal(1, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_output_publications;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_output_import_chunks;"));
            Assert.Equal("e", await TextScalarAsync(schemaName, """
                SELECT attstorage::text
                FROM pg_attribute
                WHERE attrelid = 'raster_data'::regclass
                  AND attname = 'raster';
                """));
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task RegisterPostgisAsync_HeterogeneousLayer_RollsBackNewRasterAndVisibility()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(
            nameof(PostgresRasterOutputRegistrationIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await ExecuteAsync(schemaName, """
                INSERT INTO raster_data (layer_id, name, raster)
                VALUES (
                    522,
                    'existing-3857',
                    ST_AddBand(
                        ST_MakeEmptyRaster(64, 64, 0, 64, 1, -1, 0, 0, 3857),
                        '8BUI',
                        7,
                        0));
                """);
            var bytes = await CreateGeoTiffAsync(width: 64, height: 64, pixelType: "8BUI");
            var descriptor = CreateDescriptor(
                bytes.LongLength,
                Sha256(bytes),
                width: 64,
                height: 64,
                dataType: "Byte",
                noDataValue: "0");
            var objectStore = Substitute.For<IRasterOutputObjectStore>();
            objectStore.OpenReadAsync(
                    descriptor.StoreReference,
                    descriptor.ObjectKey,
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false)));
            var registry = CreateRegistry(schemaName, objectStore);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                registry.RegisterAtomicallyAsync(CreateCommand(descriptor, "rast-import-heterogeneous")));

            Assert.Equal(1, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_data;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_statistics;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_overviews;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_footprints;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_output_publications;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_output_import_chunks;"));
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task RegisterPostgisAsync_AbovePrevious50MiBLimit_DecodeFailureRollsBackEveryStage()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(
            nameof(PostgresRasterOutputRegistrationIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            const long encodedBytes = (50L * 1024L * 1024L) + 17;
            var checksum = ComputePatternSha256(encodedBytes, 0x5a);
            var descriptor = CreateDescriptor(
                encodedBytes,
                checksum,
                width: 4096,
                height: 4096,
                dataType: "Float32",
                noDataValue: null);
            var source = new RepeatingByteStream(encodedBytes, 0x5a);
            var objectStore = Substitute.For<IRasterOutputObjectStore>();
            objectStore.OpenReadAsync(
                    descriptor.StoreReference,
                    descriptor.ObjectKey,
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Stream?>(source));
            var registry = CreateRegistry(schemaName, objectStore);

            await Assert.ThrowsAsync<PostgresException>(() =>
                registry.RegisterAtomicallyAsync(CreateCommand(descriptor, "rast-import-large-failure")));

            Assert.InRange(source.MaximumReadRequest, 1, PostgresRasterOutputRegistry.PostgisImportChunkSizeBytes);
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_data;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_statistics;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_overviews;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_footprints;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_output_publications;"));
            Assert.Equal(0, await ScalarAsync(schemaName, "SELECT COUNT(*) FROM raster_output_import_chunks;"));
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private PostgresRasterOutputRegistry CreateRegistry(
        string schemaName,
        IRasterOutputObjectStore objectStore) => new(
        new FixtureConnectionProvider(fixture.DataSource),
        objectStore,
        Options.Create(new CloudStorageOptions()),
        Options.Create(new RasterOutputPublicationOptions { StoreReference = "gp-results" }),
        schemaName);

    private static RasterOutputRegistrationCommand CreateCommand(
        ObjectStoreRasterOutputDescriptor descriptor,
        string idempotencyKey) => new()
    {
        IdempotencyKey = idempotencyKey,
        PublishedObject = descriptor,
        Target = new RasterOutputRegistrationTarget(
            RasterOutputRegistrationKind.PostgisRaster,
            $"layer.{LayerId}")
    };

    private static ObjectStoreRasterOutputDescriptor CreateDescriptor(
        long encodedBytes,
        string checksum,
        int width,
        int height,
        string dataType,
        string? noDataValue) => new()
    {
        ArtifactId = "rast_0123456789abcdef",
        OutputName = "durable-import.tif",
        Content = new RasterContentIdentity
        {
            SizeBytes = encodedBytes,
            MediaType = "image/tiff",
            Checksum = new RasterChecksum("sha256", checksum)
        },
        Grid = new RasterGridMetadata
        {
            Crs = "EPSG:4326",
            Width = width,
            Height = height,
            BandCount = 1,
            GeoTransform = [0, 1, 0, height, 0, -1],
            Bands = [new RasterBandMetadata { DataType = dataType, NoDataValue = noDataValue }],
            DecodedSizeBytes = checked((long)width * height * (dataType == "Float32" ? 4 : 1))
        },
        Engine = new RasterProducingEngine("gdal", "3.13.1"),
        Lineage = new RasterOutputLineage
        {
            JobId = "job-raster-import",
            Attempt = 1,
            ProcessId = "raster.import"
        },
        Retention = new RasterOutputRetention(
            DateTimeOffset.Parse("2026-08-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-08-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
        StoreReference = "gp-results",
        ObjectKey = "raster/published/01/rast_0123456789abcdef.tif",
        ObjectVersion = "sha256-0123456789abcdef",
        Encoding = RasterOutputEncoding.CloudOptimizedGeoTiff
    };

    private async Task<byte[]> CreateGeoTiffAsync(int width, int height, string pixelType)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ST_AsGDALRaster(
                ST_AddBand(
                    ST_MakeEmptyRaster(@width, @height, 0, @height, 1, -1, 0, 0, 4326),
                    @pixel_type,
                    7,
                    0),
                'GTiff',
                ARRAY['COMPRESS=NONE'])
            """;
        command.Parameters.AddWithValue("@width", width);
        command.Parameters.AddWithValue("@height", height);
        command.Parameters.AddWithValue("@pixel_type", pixelType);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private async Task CreateSchemaAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE layers (layer_id INTEGER PRIMARY KEY);
            INSERT INTO layers (layer_id) VALUES (522);

            CREATE TABLE raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL REFERENCES layers(layer_id) ON DELETE CASCADE,
                name VARCHAR(255) NOT NULL,
                raster raster NOT NULL,
                width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
                height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
                band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
                srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
            );

            CREATE TABLE raster_statistics (
                id BIGSERIAL PRIMARY KEY,
                raster_data_id BIGINT NOT NULL REFERENCES raster_data(id) ON DELETE CASCADE,
                band_number INTEGER NOT NULL,
                min_value DOUBLE PRECISION,
                max_value DOUBLE PRECISION,
                mean_value DOUBLE PRECISION,
                std_dev DOUBLE PRECISION,
                valid_pixel_count BIGINT,
                nodata_pixel_count BIGINT,
                computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (raster_data_id, band_number)
            );

            CREATE TABLE raster_overviews (
                id BIGSERIAL PRIMARY KEY,
                raster_data_id BIGINT NOT NULL REFERENCES raster_data(id) ON DELETE CASCADE,
                overview_factor INTEGER NOT NULL,
                raster raster NOT NULL,
                ground_resolution DOUBLE PRECISION NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (raster_data_id, overview_factor)
            );

            CREATE TABLE raster_footprints (
                raster_data_id BIGINT PRIMARY KEY REFERENCES raster_data(id) ON DELETE CASCADE,
                footprint geometry NOT NULL,
                seamline geometry,
                srid INTEGER NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ
            );

            CREATE TABLE raster_output_import_chunks (
                idempotency_key VARCHAR(80) NOT NULL,
                chunk_ordinal INTEGER NOT NULL,
                content BYTEA NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (idempotency_key, chunk_ordinal)
            );

            CREATE TABLE raster_output_publications (
                idempotency_key VARCHAR(80) PRIMARY KEY,
                store_reference VARCHAR(128) NOT NULL,
                object_key VARCHAR(1024) NOT NULL,
                object_version VARCHAR(256) NOT NULL,
                checksum_algorithm VARCHAR(16) NOT NULL,
                checksum_value VARCHAR(128) NOT NULL,
                size_bytes BIGINT NOT NULL,
                media_type VARCHAR(127) NOT NULL,
                target_kind VARCHAR(32) NOT NULL,
                target_reference VARCHAR(128) NOT NULL,
                published_descriptor JSONB NOT NULL,
                output_descriptor JSONB NOT NULL,
                visible_at TIMESTAMPTZ NOT NULL,
                expires_at TIMESTAMPTZ NOT NULL,
                UNIQUE (store_reference, object_key)
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string schemaName, string sql)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> TextScalarAsync(string schemaName, string sql)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string schemaName, string sql)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ComputePatternSha256(long length, byte value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        Array.Fill(buffer, value);
        long remaining = length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class RepeatingByteStream(long length, byte value) : Stream
    {
        private long _position;

        public int MaximumReadRequest { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            if (read <= 0)
            {
                return 0;
            }

            MaximumReadRequest = Math.Max(MaximumReadRequest, count);
            buffer.AsSpan(offset, read).Fill(value);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - _position);
            if (read <= 0)
            {
                return ValueTask.FromResult(0);
            }

            MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
            buffer.Span[..read].Fill(value);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }
}
