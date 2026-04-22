// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Tests for the import schema suggestion service.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class ImportSchemaSuggestionServiceTests
{
    private readonly ImportSchemaSuggestionService _service = new();

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_ShapefileWithSrid_RecommendsSridAndSpatialIndex()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.Shapefile,
            TotalFeatureCount = 500,
            DetectedSrid = 4326,
            SampleProperties = new Dictionary<string, object?>
            {
                ["objectid"] = 1,
                ["parcel_id"] = "P001",
                ["zoning_code"] = "R1",
                ["area_sqft"] = 5000.5,
            },
        };

        var result = await _service.SuggestAsync(preview, "parcels.zip");

        result.SourceName.Should().Be("parcels.zip");
        result.DetectedFormat.Should().Be("Shapefile");
        result.Srid.RecommendedSrid.Should().Be(4326);
        result.Srid.DetectedSrid.Should().Be(4326);
        result.Srid.RequiresTransformation.Should().BeFalse();
        result.Indexes.Should().Contain(i => i.Type == IndexSuggestionType.Spatial);
        result.Indexes.Should().Contain(i =>
            i.Type == IndexSuggestionType.BTree && i.Columns.Contains("objectid"));
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_NoDetectedSrid_DefaultsToWgs84()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.GeoJson,
            TotalFeatureCount = 10,
            DetectedSrid = null,
            SampleProperties = new Dictionary<string, object?> { ["name"] = "test" },
        };

        var result = await _service.SuggestAsync(preview, "data.geojson");

        result.Srid.RecommendedSrid.Should().Be(4326);
        result.Srid.Reason.Should().Contain("WGS 84");
        result.Srid.Reason.Should().Contain("RFC 7946");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_NoDetectedSrid_NonGeoJson_OmitsRfc7946Reference()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.Shapefile,
            TotalFeatureCount = 10,
            DetectedSrid = null,
            SampleProperties = new Dictionary<string, object?> { ["name"] = "test" },
        };

        var result = await _service.SuggestAsync(preview, "data.zip");

        result.Srid.RecommendedSrid.Should().Be(4326);
        result.Srid.Reason.Should().Contain("WGS 84");
        result.Srid.Reason.Should().NotContain("RFC 7946");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_WebMercator_KeepsButNotes()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.Shapefile,
            TotalFeatureCount = 100,
            DetectedSrid = 3857,
            SampleProperties = new Dictionary<string, object?> { ["id"] = 1 },
        };

        var result = await _service.SuggestAsync(preview, "tiles.zip");

        result.Srid.RecommendedSrid.Should().Be(3857);
        result.Srid.Reason.Should().Contain("Web Mercator");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_IdFields_GetBTreeIndexSuggestion()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.GeoJson,
            TotalFeatureCount = 100,
            DetectedSrid = 4326,
            SampleProperties = new Dictionary<string, object?>
            {
                ["fid"] = 1,
                ["category_type"] = "residential",
                ["status_code"] = "A",
            },
        };

        var result = await _service.SuggestAsync(preview, "features.geojson");

        result.Indexes.Should().Contain(i =>
            i.Type == IndexSuggestionType.BTree && i.Columns.Contains("fid"));
        result.Indexes.Should().Contain(i =>
            i.Type == IndexSuggestionType.BTree && i.Columns.Contains("category_type"));
        result.Indexes.Should().Contain(i =>
            i.Type == IndexSuggestionType.BTree && i.Columns.Contains("status_code"));
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_NameField_GetsGinIndexSuggestion()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.GeoJson,
            TotalFeatureCount = 100,
            DetectedSrid = 4326,
            SampleProperties = new Dictionary<string, object?>
            {
                ["name"] = "Downtown Park",
            },
        };

        var result = await _service.SuggestAsync(preview, "parks.geojson");

        result.Indexes.Should().Contain(i =>
            i.Type == IndexSuggestionType.Gin && i.Columns.Contains("name"));
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_DateStringField_SuggestsTimestampType()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.Csv,
            TotalFeatureCount = 50,
            DetectedSrid = 4326,
            SampleProperties = new Dictionary<string, object?>
            {
                ["created_at"] = "2024-06-15T10:30:00Z",
            },
        };

        var result = await _service.SuggestAsync(preview, "events.csv");

        result.FieldTypes.Should().Contain(f =>
            f.FieldName == "created_at" && f.RecommendedType == "TIMESTAMP WITH TIME ZONE");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_UuidStringField_SuggestsUuidType()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.GeoJson,
            TotalFeatureCount = 50,
            DetectedSrid = 4326,
            SampleProperties = new Dictionary<string, object?>
            {
                ["guid"] = "550e8400-e29b-41d4-a716-446655440000",
            },
        };

        var result = await _service.SuggestAsync(preview, "items.geojson");

        result.FieldTypes.Should().Contain(f =>
            f.FieldName == "guid" && f.RecommendedType == "UUID");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_Observations_IncludeFormatAndCount()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.GeoPackage,
            TotalFeatureCount = 1500,
            DetectedSrid = 4326,
            SampleProperties = new Dictionary<string, object?> { ["id"] = 1 },
            AvailableLayers = ["roads", "buildings"],
        };

        var result = await _service.SuggestAsync(preview, "city.gpkg");

        result.Observations.Should().Contain(o => o.Contains("city.gpkg"));
        result.Observations.Should().Contain(o => o.Contains("GeoPackage"));
        result.Observations.Should().Contain(o => o.Contains("1500"));
        result.Observations.Should().Contain(o => o.Contains("Multi-layer"));
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_AlwaysIncludesSpatialIndex()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.GeoJson,
            TotalFeatureCount = 1,
            DetectedSrid = 4326,
            SampleProperties = new Dictionary<string, object?>(),
        };

        var result = await _service.SuggestAsync(preview, "single.geojson");

        result.Indexes.Should().Contain(i => i.Type == IndexSuggestionType.Spatial);
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public async Task Suggest_ProjectedSrid_KeepsAndNotesTransformation()
    {
        var preview = new FilePreview
        {
            Format = SupportedFileFormat.Shapefile,
            TotalFeatureCount = 100,
            DetectedSrid = 2913, // Oregon State Plane North
            SampleProperties = new Dictionary<string, object?> { ["id"] = 1 },
        };

        var result = await _service.SuggestAsync(preview, "oregon.zip");

        result.Srid.RecommendedSrid.Should().Be(2913);
        result.Srid.DetectedSrid.Should().Be(2913);
        result.Srid.RequiresTransformation.Should().BeFalse();
        result.Srid.Reason.Should().Contain("EPSG:2913");
    }
}
