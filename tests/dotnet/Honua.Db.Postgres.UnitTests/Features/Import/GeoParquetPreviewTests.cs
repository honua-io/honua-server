// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoParquetPreviewTests
{
    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_ReturnsPreviewMetadata()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_NoCrsKey_DefaultsToSrid4326()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.Omitted);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "no_crs.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_ExplicitNullCrs_ReturnsNullSrid()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.ExplicitNull);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "null_crs.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().BeNull();
        preview.TotalFeatureCount.Should().Be(1);
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_OgcCrs84ProjJson_DefaultsToSrid4326()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.OgcCrs84ProjJson);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "ogc_crs84_projjson.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
    }

    [UnitTheory]
    [InlineData(GeoParquetTestFactory.CrsStyle.PropertiesName)]
    [InlineData(GeoParquetTestFactory.CrsStyle.OgcCrs84PropertiesName)]
    public async Task PreviewFileAsync_GeoParquet_LegacyGeoJsonCrs_RejectsMalformedProjJson(GeoParquetTestFactory.CrsStyle style)
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: style);
        var act = () => CreateService().PreviewFileAsync(stream, "legacy-crs.parquet");
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*PROJJSON*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_DecimalEpsgCode_RejectsMalformedProjJson()
    {
        // An identifier-only object cannot establish a valid source CRS.
        var crs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["authority"] = "EPSG",
                ["code"] = 4326.5
            }
        };
        await using var stream = await GeoParquetTestFactory.CreateWithCustomCrsAsync(crs);
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "decimal_crs.parquet");
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*PROJJSON*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_OutOfRangeEpsgCode_RejectsMalformedProjJson()
    {
        // Out-of-range identifiers cannot substitute for a complete CRS definition.
        var crs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["authority"] = "EPSG",
                ["code"] = 99999999999L
            }
        };
        await using var stream = await GeoParquetTestFactory.CreateWithCustomCrsAsync(crs);
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "huge_crs.parquet");
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*PROJJSON*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_NonWkbEncoding_ThrowsNotSupportedException()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(encoding: "point");
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "non_wkb.parquet");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*supports only WKB geometry encoding*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_SecondaryGeometryColumn_RejectsLossyPreview()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithSecondaryGeometryAsync();
        var service = CreateService();
        var act = () => service.PreviewFileAsync(stream, "multi_geom.parquet");

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*multiple geometry columns*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_MalformedGeoJson_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithMalformedMetadataAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "bad_meta.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*malformed*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_NoGeoMetadataKey_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithoutGeoMetadataAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "plain.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*missing required*geo*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_WrongShapedMetadata_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithWrongShapedMetadataAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "wrong_shape.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*unexpected structure*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_TotalFeatureCount_ReflectsFullRowCount()
    {
        // Create a file with 5 rows but cap preview samples at 2.
        // TotalFeatureCount comes from Parquet footer metadata (5),
        // not the sample size (2).
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(rowCount: 5);
        var service = CreateService(maxPreviewFeatures: 2);

        var preview = await service.PreviewFileAsync(stream, "multi_row.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(5);
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_NullGeometryRow_RetainsIdAndAttributesInSample()
    {
        // Put the null-geometry row first so the preview's single property sample must contain it.
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            includeNullGeometryRow: true, nullGeometryFirst: true);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "null_geom.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(2);
        preview.SampleProperties["objectid"].Should().Be(2L);
        preview.SampleProperties["name"].Should().Be("No Geometry");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_TimeAndBinaryColumns_IncludedInSampleProperties()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithTimeAndBinaryColumnsAsync();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "time_binary.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("event_time");
        // byte[] is converted to base64 in preview path
        preview.SampleProperties.Should().ContainKey("thumbnail");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_Int16Column_IncludedInSampleProperties()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithInt16ColumnAsync();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "int16.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("priority");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_ProjJsonStringEpsgCode_DetectsSrid4326()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.ProjJsonStringCode);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "string_code.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_MissingPrimaryColumn_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithMissingPrimaryColumnAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "no_primary_col.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*primary_column*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_MissingColumnsField_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithMissingColumnsFieldAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "no_columns.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*columns*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_LargeSingleRowGroup_IsAccepted()
    {
        // pyarrow/GeoPandas defaults to a single row group up to 1,048,576 rows.
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            rowCount: 150_000);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "large_rg.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(150_000);
        preview.SampleProperties.Should().ContainKey("name");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_DuckDbSizedRowGroups_AreAccepted()
    {
        // DuckDB defaults to 122,880-row groups. Exercise both groups around that
        // boundary instead of requiring clients to re-export with smaller groups.
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            rowCount: 150_000,
            rowGroupSize: 122_880);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "duckdb.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(150_000);
        preview.SampleProperties.Should().ContainKey("name");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_RowGroupExceedingMemoryLimit_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(rowCount: 150_000);
        var service = CreateService(maxMemoryBytes: 1);

        var act = () => service.PreviewFileAsync(stream, "oversized_rg.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*ImportLimits.MaxMemoryBytes*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_MismatchedPrimaryColumn_ThrowsInvalidDataException()
    {
        // Geo metadata references "geometry" but the Parquet schema has no such column
        await using var stream = await GeoParquetTestFactory.CreateWithMismatchedPrimaryColumnAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "mismatched.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*geometry*not found*schema*");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_PrimaryColumnMissingFromGeoColumns_ThrowsInvalidDataException()
    {
        // Parquet schema has "geometry", but geo.columns only describes "other_geom"
        await using var stream = await GeoParquetTestFactory.CreateWithPrimaryColumnMissingFromGeoColumnsAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "missing_in_columns.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*primary_column*columns*");
    }

    [UnitTest]
    public void DetectFormat_AndSupportedExtensions_IncludeGeoParquet()
    {
        var service = CreateService();

        service.DetectFormat("sample.parquet").Should().Be(SupportedFileFormat.GeoParquet);
        service.DetectFormat("sample.geoparquet").Should().Be(SupportedFileFormat.GeoParquet);
        service.GetSupportedExtensions().Should().Contain(".parquet");
        service.GetSupportedExtensions().Should().Contain(".geoparquet");
    }

    [UnitTest]
    public void DetectFormat_UppercaseExtension_StillDetectsGeoParquet()
    {
        var service = CreateService();

        service.DetectFormat("SAMPLE.PARQUET").Should().Be(SupportedFileFormat.GeoParquet);
        service.DetectFormat("data.GeoParquet").Should().Be(SupportedFileFormat.GeoParquet);
        service.DetectFormat("mixed.Parquet").Should().Be(SupportedFileFormat.GeoParquet);
    }

    [UnitTest]
    public void DetectFormat_UppercaseExtension_WorksForPreExistingFormats()
    {
        var service = CreateService();

        service.DetectFormat("data.GEOJSON").Should().Be(SupportedFileFormat.GeoJson);
        service.DetectFormat("ARCHIVE.GPKG").Should().Be(SupportedFileFormat.GeoPackage);
        service.DetectFormat("tracks.GPX").Should().Be(SupportedFileFormat.Gpx);
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquet_CorruptedFile_ThrowsInvalidDataException()
    {
        // A stream whose contents are not a valid Parquet file. Parquet.Net throws
        // IOException for invalid magic bytes; GeoParquetReader must normalize this
        // to InvalidDataException so the endpoint returns 400, not 500.
        await using var stream = new MemoryStream("not a parquet file"u8.ToArray());
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "corrupted.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*not a valid Parquet*");
    }

    private static IFileImportService CreateService(
        int? maxPreviewFeatures = null,
        long? maxMemoryBytes = null)
    {
        var limits = maxPreviewFeatures.HasValue || maxMemoryBytes.HasValue
            ? new ImportLimits
            {
                MaxPreviewFeatures = maxPreviewFeatures ?? ImportLimits.Default.MaxPreviewFeatures,
                MaxMemoryBytes = maxMemoryBytes ?? ImportLimits.Default.MaxMemoryBytes
            }
            : null;

        return PreviewImportServiceFactory.Create(limits);
    }
}
