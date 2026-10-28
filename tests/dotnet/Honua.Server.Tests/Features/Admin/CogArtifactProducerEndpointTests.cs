// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Actual PostGIS COG publication bytes, separate from substituted transport tests.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Export)]
public sealed class CogArtifactProducerEndpointTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("POST /api/v1/admin/raster-artifacts/cog")]
    [Endpoint("GET /api/v1/rasters/cog/{*artifactId}")]
    [Endpoint("HEAD /api/v1/rasters/cog/{*artifactId}")]
    public async Task PublishCog_RealProducer_StoresAndServesOptimizedBytes(bool missingDriver)
    {
        var database = new PostgresFixture();
        await database.InitializeAsync();
        var schema = await database.CreateIsolatedSchemaAsync(nameof(CogArtifactProducerEndpointTests));
        WebAppFixture? host = null;
        NpgsqlDataSource? restricted = null;
        try
        {
            await using (var connection = await database.GetConnectionAsync(schema))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE raster_data (
                        id BIGSERIAL PRIMARY KEY, layer_id INTEGER NOT NULL, name VARCHAR(255) NOT NULL,
                        raster raster NOT NULL, acquisition_date TIMESTAMPTZ,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), updated_at TIMESTAMPTZ,
                        width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
                        height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
                        band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
                        pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster,1)) STORED,
                        srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED);
                    INSERT INTO raster_data(layer_id,name,raster)
                    VALUES (0,'all-nodata-primary',ST_AddBand(
                        ST_MakeEmptyRaster(1024,1024,0,1024,1,-1,0,0,3857),
                        '32BF'::text,-9999::double precision,-9999::double precision));
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var dataSource = database.DataSource;
            if (missingDriver)
            {
                restricted = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(dataSource.ConnectionString)
                { Options = "-c postgis.gdal_enabled_drivers=GTiff" }.ConnectionString);
                dataSource = restricted;
                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT count(*) FILTER (WHERE short_name='COG'),
                           count(*) FILTER (WHERE short_name='GTiff' AND can_write)
                    FROM ST_GDALDrivers();
                    """;
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    (await reader.ReadAsync()).Should().BeTrue();
                    reader.GetInt64(0).Should().Be(0);
                    reader.GetInt64(1).Should().Be(1);
                }
                command.CommandText = """
                    SELECT octet_length(ST_AsGDALRaster(ST_AddBand(
                        ST_MakeEmptyRaster(2,2,0,2,1,-1,0,0,3857),
                        '32BF'::text,1::double precision,-9999::double precision),'GTiff'));
                    """;
                Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
            }
            var store = new PostgresRasterStore(new FixtureConnectionProvider(dataSource),
                NullLogger<PostgresRasterStore>.Instance, FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var primary = await store.GetPrimaryRasterInfoAsync(WebAppFixture.TestLayerId);
            primary.Should().NotBeNull();
            host = new WebAppFixture().ReplaceService<IRasterStore>(store).ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
            await host.InitializeAsync();
            using var client = host.CreateAdminClient();
            var storage = host.GetService<ICloudFileStorage>();
            var artifactId = $"cog/{WebAppFixture.TestLayerId}/{primary!.Id}.tif";
            (await storage.GetMetadataAsync(artifactId)).Should().BeNull();
            using var response = await client.PostAsJsonAsync("/api/v1/admin/raster-artifacts/cog",
                new { layerId = WebAppFixture.TestLayerId });
            var text = await response.Content.ReadAsStringAsync();
            if (missingDriver)
            {
                (await storage.GetMetadataAsync(artifactId)).Should().BeNull("a failed COG export must not publish fallback TIFF");
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, text);
                text.Should().Contain("COG export failed.");
                text.Should().NotContain("cloud-optimized");
                return;
            }
            response.StatusCode.Should().Be(HttpStatusCode.Created, text);
            using var descriptor = JsonDocument.Parse(text);
            descriptor.RootElement.GetProperty("artifactId").GetString().Should().Be(artifactId);
            var url = descriptor.RootElement.GetProperty("url").GetString()!;
            var stored = await storage.GetMetadataAsync(artifactId);
            stored.Should().NotBeNull();
            stored!.ContentType.Should().Be("image/tiff; application=geotiff; profile=cloud-optimized");
            await using var stream = await storage.DownloadAsync(artifactId);
            stream.Should().NotBeNull();
            using var copied = new MemoryStream();
            await stream!.CopyToAsync(copied);
            var bytes = copied.ToArray();
            using var downloaded = await client.GetAsync(url);
            downloaded.StatusCode.Should().Be(HttpStatusCode.OK);
            (await downloaded.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            head.Content.Headers.ContentLength.Should().Be(bytes.LongLength);
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 31);
            using var range = await client.SendAsync(rangeRequest);
            range.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            (await range.Content.ReadAsByteArrayAsync()).Should().Equal(bytes[..32]);
            // Producer-stack roundtrip verifies samples; external validators consume the
            // optional exact-byte artifact separately and remain independent evidence.
            await using (var connection = await database.GetConnectionAsync(schema))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    WITH output AS (SELECT ST_FromGDALRaster(@bytes) AS rast)
                    SELECT ST_Count(rast,1,true),ST_Value(rast,1,1,1,false),
                           ST_Value(rast,1,1024,1024,false) FROM output;
                    """;
                command.Parameters.AddWithValue("bytes", bytes);
                await using var reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetInt64(0).Should().Be(0, "the published canvas is entirely nodata");
                reader.GetDouble(1).Should().Be(-9999);
                reader.GetDouble(2).Should().Be(-9999);
            }
            var ranges = Substitute.For<ICloudRangeReader>();
            ranges.ReadRangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(bytes.AsSpan((int)call.ArgAt<long>(2),
                    Math.Min(call.ArgAt<int>(3), bytes.Length - (int)call.ArgAt<long>(2))).ToArray()));
            var metadata = await new CogMetadataExtractor().ReadMetadataAsync(ranges, "test", "published.tif");
            metadata.Width.Should().Be(1024);
            metadata.Height.Should().Be(1024);
            metadata.Srid.Should().Be(3857);
            metadata.NoData.Should().Be("-9999");
            metadata.OverviewLevels.Should().Contain(level => level.Width <= 512 && level.Height <= 512);
            metadata.OverviewLevels.Should().OnlyContain(level => level.TileOffsets.Length > 0);
            var evidence = Environment.GetEnvironmentVariable("HONUA_CNG_COG_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllBytesAsync(Path.Combine(evidence, "published-all-nodata.tif"), bytes);
                await File.WriteAllTextAsync(Path.Combine(evidence, "published-all-nodata.sha256"),
                    Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            if (restricted is not null) await restricted.DisposeAsync();
            await database.DropSchemaAsync(schema);
            await database.DisposeAsync();
        }
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
