// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using TestOperations = Honua.TestKit.Constants.Operations;

namespace Honua.Db.Postgres.Tests.Features.Raster;

// A scanned COG's decode fields and GDAL NoData survive the catalog, so ImageServer statistics,
// identify and export read the COG the way the scan saw it.
[Collection("Database")]
[Protocol(ProtocolNames.TestQuality)]
public sealed class PostgresCogStoreDecodeMetadataTests(PostgresFixture fixture)
{
    private const int LayerId = 8412;

    [IntegrationTest]
    [Operation(TestOperations.Query)]
    public async Task UpdateMetadataAsync_ScannedDecodeFieldsAndNoData_ReadBackFromTheCatalog()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresCogStoreDecodeMetadataTests));
        try
        {
            await CreateCatalogAsync(schema);
            var store = new PostgresCogStore(new TestDatabaseConnectionProvider(fixture.DataSource, () => schema), NullLogger<PostgresCogStore>.Instance, schema);
            var registration = await store.RegisterAsync(Request());
            var scanned = Metadata() with
            {
                BitsPerSample = 16,
                Predictor = 2,
                IsLittleEndian = false,
                PhotometricInterpretation = 2,
                PlanarConfiguration = 2,
                NoData = "-9999",
            };

            await store.UpdateMetadataAsync(registration.Id, scanned, ifdCache: null);

            var read = (await store.GetAsync(registration.Id))!.Metadata!;
            read.Should().BeEquivalentTo(scanned, options => options.Excluding(metadata => metadata.OverviewLevels));
            (await store.ListByLayerAsync(LayerId)).Single().Metadata!.NoData.Should().Be("-9999");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    [Operation(TestOperations.Query)]
    public async Task GetAsync_RowScannedBeforeTheDecodeColumns_KeepsTheRecordDefaults()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresCogStoreDecodeMetadataTests));
        try
        {
            await CreateCatalogAsync(schema);
            var store = new PostgresCogStore(new TestDatabaseConnectionProvider(fixture.DataSource, () => schema), NullLogger<PostgresCogStore>.Instance, schema);
            var registration = await store.RegisterAsync(Request());
            await store.UpdateMetadataAsync(registration.Id, Metadata(), ifdCache: null);
            await ExecuteAsync(schema, "UPDATE cloud_raster_catalog SET bits_per_sample = NULL, predictor = NULL, is_little_endian = NULL, photometric_interpretation = NULL, planar_configuration = NULL, nodata = NULL");

            var read = (await store.GetAsync(registration.Id))!.Metadata!;

            read.BitsPerSample.Should().Be(8);
            read.Predictor.Should().Be(1);
            read.PhotometricInterpretation.Should().Be(1);
            read.IsLittleEndian.Should().BeTrue();
            read.PlanarConfiguration.Should().Be(1);
            read.NoData.Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static CogRegistrationRequest Request() => new()
    {
        LayerId = LayerId,
        Name = "roster",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "bucket",
        ObjectKey = "cog/roster-3857.tif",
    };

    private static CogMetadata Metadata() => new(
        512,
        512,
        1,
        "uint8",
        3857,
        "DEFLATE",
        256,
        256,
        [],
        new RasterExtent
        {
            XMin = 0,
            YMin = 0,
            XMax = 19568.87,
            YMax = 19568.87,
            Srid = 3857,
        });

    // The 018 catalog columns the store reads (without its layer foreign key), then migration 223.
    private async Task CreateCatalogAsync(string schema)
    {
        await ExecuteAsync(schema, """
            CREATE TABLE cloud_raster_catalog (
                id BIGSERIAL PRIMARY KEY, layer_id INTEGER NOT NULL, name VARCHAR(255) NOT NULL,
                description TEXT, provider VARCHAR(50) NOT NULL, bucket VARCHAR(255) NOT NULL,
                object_key VARCHAR(1024) NOT NULL, width INTEGER, height INTEGER, band_count INTEGER,
                pixel_type VARCHAR(10), srid INTEGER, compression VARCHAR(50), tile_width INTEGER,
                tile_height INTEGER, overview_levels JSONB, extent_xmin DOUBLE PRECISION,
                extent_ymin DOUBLE PRECISION, extent_xmax DOUBLE PRECISION, extent_ymax DOUBLE PRECISION,
                ifd_cache BYTEA, metadata_scanned_at TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ)
            """);
        var migration = Directory.EnumerateFiles(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations"), "223_AddCloudRasterDecodeMetadata.sql").Single();
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(migration)).Replace("honua.cloud_raster_catalog", $"\"{schema}\".cloud_raster_catalog", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
