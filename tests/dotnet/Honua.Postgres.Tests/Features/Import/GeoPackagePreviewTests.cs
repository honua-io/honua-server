// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.TestKit.Infrastructure;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoPackagePreviewTests
{
    [Fact]
    public async Task PreviewFileAsync_GeoPackage_ReturnsLayerMetadataAndAttributes()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.AvailableLayers.Should().Contain("sample_layer");
            preview.DetectedSrid.Should().Be(4326);
            preview.TotalFeatureCount.Should().Be(1);
            preview.SampleProperties.Should().ContainKey("name");
            preview.SampleProperties["name"].Should().Be("Test Feature");
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    [Fact]
    public async Task PreviewFileAsync_GeoPackage_ResolvesLocalSrsIdViaSpatialRefSys()
    {
        // A spec-legal GeoPackage may number its srs_id locally (here srs_id=1) while the
        // gpkg_spatial_ref_sys row maps it to EPSG:27700 via organization_coordsys_id. Reading
        // srs_id as the EPSG code directly (the old behavior) would mis-georeference every
        // feature; the resolver must return 27700, not 1 (#2743).
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, srsId: 1, organization: "EPSG", organizationCoordsysId: 27700);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.DetectedSrid.Should().Be(27700);
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    [Fact]
    public async Task PreviewFileAsync_GeoPackage_NonEpsgOrganizationIsUndetected()
    {
        // A non-EPSG authority (or custom WKT-only CRS) must not be guessed as an EPSG code;
        // the resolver returns null so the import requires an explicit source SRID (#2743).
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, srsId: 4001, organization: "VENDOR", organizationCoordsysId: 4001);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.DetectedSrid.Should().BeNull();
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    [Fact]
    public async Task PreviewFileAsync_GeoPackageWithoutSpatialRefSysTable_FallsBackToRawSrsId()
    {
        // A malformed GeoPackage missing the (spec-required) gpkg_spatial_ref_sys table must not
        // throw "no such table" when enumerating layers. The reader probes for the table and, when
        // absent, falls back to a join-less query that treats the raw srs_id as a best-effort EPSG
        // code; import-path SRID validation still guards nonsense codes downstream (#2743).
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, srsId: 4326, includeSpatialRefSys: false);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.AvailableLayers.Should().Contain("sample_layer");
            preview.TotalFeatureCount.Should().Be(1);
            preview.DetectedSrid.Should().Be(4326);
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    [Fact]
    public async Task PreviewFileAsync_GeoPackage_ReturnsAllAvailableLayers()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, includeSecondLayer: true);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.AvailableLayers.Should().BeEquivalentTo(["sample_layer", "second_layer"]);
            preview.TotalFeatureCount.Should().Be(1, "preview samples the first layer until source-layer selection is supported");
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    [Fact]
    public async Task ImportFileAsync_GeoPackageWithMultipleLayers_FailsWithClearLayerMessage()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath, includeSecondLayer: true);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "sample.gpkg",
                TableName = "multi_layer_gpkg",
                TargetSrid = 4326,
                OverwriteExisting = true
            });

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Be("Invalid GeoPackage import request.");
        }
        finally
        {
            await DeleteGeoPackageAsync(filePath);
        }
    }

    private static void CreateGeoPackage(
        string filePath,
        bool includeSecondLayer = false,
        int srsId = 4326,
        string organization = "EPSG",
        int organizationCoordsysId = 4326,
        bool includeSpatialRefSys = true) =>
        GeoPackageTestFiles.Create(filePath, includeSecondLayer, srsId, organization, organizationCoordsysId, includeSpatialRefSys);

    private static IFileImportService CreateService() =>
        PreviewImportServiceFactory.Create();

    private static Task DeleteGeoPackageAsync(string filePath) =>
        GeoPackageTestFiles.DeleteAsync(filePath);
}
