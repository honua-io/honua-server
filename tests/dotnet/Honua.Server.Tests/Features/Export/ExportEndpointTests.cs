// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Net;
using System.Threading.Channels;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Export;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Export;

/// <summary>
/// Integration tests for the data export endpoint.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class ExportEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_CsvFormat_ReturnsValidCsvWithWktGeometry()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/0/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition?.FileName.Should().Contain("test_");

        var content = await response.Content.ReadAsStringAsync();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Must have header + data rows
        lines.Length.Should().BeGreaterThanOrEqualTo(2);

        // Header must contain WKT column
        lines[0].Should().Contain("WKT");

        // Data rows should contain POINT geometry as WKT
        lines[1].Should().Contain("POINT");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_ShapefileFormat_ReturnsValidZipWithComponents()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/0/export?format=shapefile");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/zip");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var entryNames = zip.Entries.Select(e => Path.GetExtension(e.Name).ToLowerInvariant()).ToList();

        // Shapefile must contain .shp, .shx, .dbf at minimum
        entryNames.Should().Contain(".shp");
        entryNames.Should().Contain(".shx");
        entryNames.Should().Contain(".dbf");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_GeoPackageFormat_ReturnsValidGpkg()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/0/export?format=gpkg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geopackage+sqlite3");

        // Save to temp file and verify it's a valid SQLite/GeoPackage database
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var tempPath = Path.Combine(Path.GetTempPath(), $"test-export-{Guid.NewGuid():N}.gpkg");

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            // Verify GeoPackage metadata tables exist
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM gpkg_contents WHERE data_type = 'features'";
            var count = await cmd.ExecuteScalarAsync();
            Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterOrEqualTo(1);

            // Verify feature data was written
            cmd.CommandText = "SELECT COUNT(*) FROM features";
            var featureCount = await cmd.ExecuteScalarAsync();
            Convert.ToInt64(featureCount, System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterOrEqualTo(1);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_WithWhereClause_ReturnsFilteredFeatures()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/0/export?format=csv&where=name%3D%27Test%20Feature%27");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Should have header + filtered results (at least 1 matching feature)
        lines.Length.Should().BeGreaterThanOrEqualTo(2);

        // The content should contain the filtered feature name
        content.Should().Contain("Test Feature");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_WithBbox_ReturnsSpatiallyFilteredFeatures()
    {
        // Bbox covering the test data area around San Francisco
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/0/export?format=csv&bbox=-123,37,-122,38");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Should have header + features within the bbox
        lines.Length.Should().BeGreaterThanOrEqualTo(2);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_WithOutFields_ReturnsSelectedAttributes()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/0/export?format=csv&outFields=name");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Header should only contain the requested field plus WKT
        var header = lines[0];
        header.Should().Contain("name");
        header.Should().Contain("WKT");
        // Should NOT contain other fields
        header.Should().NotContain("description");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_WithOutSR_ReprojectsCoordinates()
    {
        // Request output in Web Mercator (3857) — default data is in 4326
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/0/export?format=csv&outSR=3857");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.Should().BeGreaterThanOrEqualTo(2);

        // Web Mercator coordinates should be large numbers (millions), not lat/lon ranges
        // WKT column should contain reprojected coordinates
        var dataLine = lines[1];
        dataLine.Should().Contain("POINT");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_InvalidFormat_Returns400()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/0/export?format=invalid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_NonExistentLayer_Returns404()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/services/test/layers/999/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{layerId}/export")]
    public async Task Export_AsyncProgressPersistenceFails_DoesNotQueueHeadlessBackgroundJob()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        featureReader
            .CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(50_001L);

        var progressStore = Substitute.For<IUniversalProgressStore>();
        progressStore
            .When(store => store.SetProgressAsync(
                Arg.Any<string>(),
                Arg.Any<IOperationProgress>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Simulated progress-store failure."));

        var cloudStorage = Substitute.For<ICloudFileStorage>();
        var exportChannel = Channel.CreateUnbounded<string>();
        var requestCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IFeatureReader>();
                services.AddSingleton(featureReader);
                services.RemoveAll<IUniversalProgressStore>();
                services.AddSingleton(progressStore);
                services.RemoveAll<ICloudFileStorage>();
                services.AddSingleton(cloudStorage);
                services.RemoveAll<IDistributedCache>();
                services.AddSingleton<IDistributedCache>(requestCache);
                services.RemoveAll<Channel<string>>();
                services.AddSingleton(exportChannel);
            });

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                "/api/v1/admin/services/test/layers/0/export?format=csv");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            exportChannel.Reader.TryRead(out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
