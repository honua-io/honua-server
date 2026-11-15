// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class StreamingFileImportStagingTableTests
{
    [IntegrationTheory]
    [InlineData("MultiLineString", "[[[0,0],[1,1]],[[2,2]]]")]
    [InlineData("Polygon", "[[[0,0],[4,0],[4,4],[0,0]],[[1,1],[2,2]]]")]
    [InlineData("MultiPolygon", "[[[[0,0],[4,0],[4,4],[0,0]]],[[[1,1],[2,2]]]]")]
    public async Task ImportFileAsync_CreateOnlyMalformedComponent_DiscardsDataset(string type, string coordinates)
    {
        var feature = """{"type":"Feature","geometry":{"type":""" + $"\"{type}\",\"coordinates\":{coordinates}" + """},"properties":{}}""";
        await AssertInvalidCreateOnlyGeoJsonAsync(feature);
    }

    [IntegrationTheory]
    [InlineData("Point", "[1]")]
    [InlineData("LineString", "[[0,0]]")]
    [InlineData("Polygon", "[[[0,0],[1,0],[1,1],[2,2]]]")]
    public async Task ImportFileAsync_CreateOnlyMalformedCollectionChild_DiscardsDataset(string type, string coordinates)
    {
        var feature = """{"type":"Feature","geometry":{"type":"GeometryCollection","geometries":[{"type":"Point","coordinates":[0,0]},{"type":"""
            + $"\"{type}\",\"coordinates\":{coordinates}" + """}]},"properties":{}}""";
        await AssertInvalidCreateOnlyGeoJsonAsync(feature);
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyErrorAfterWarnings_DiscardsDataset()
    {
        const string warning = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[2,2],[0,2],[2,0],[0,0]]]},"properties":{}}""";
        const string invalid = """{"type":"Feature","geometry":{"type":"Point","coordinates":[1]},"properties":{}}""";
        await AssertInvalidCreateOnlyGeoJsonAsync(string.Join(",", Enumerable.Repeat(warning, 100).Append(invalid)));
    }

    private async Task AssertInvalidCreateOnlyGeoJsonAsync(string features)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_components");
        try
        {
            await EnsureImportFunctionsAsync();
            var receiptWritten = false;
            var input = $$"""{"type":"FeatureCollection","features":[{{features}}]}""";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var result = await CreateOnlyService(schema).ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "features.geojson",
                TableName = OverwriteGuardLogicalTable,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.CreateOnly,
                CommitParticipant = (_, _) => { receiptWritten = true; return ValueTask.CompletedTask; },
            });
            result.Success.Should().BeFalse();
            receiptWritten.Should().BeFalse();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeFalse();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyGpxWithSkippedWaypoint_DiscardsDatasetWithoutReceipt()
    {
        const string input = """
            <gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">
              <wpt lat="1" lon="2"/><wpt lat="bad" lon="3"/>
            </gpx>
            """;
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_parser");
        try
        {
            await EnsureImportFunctionsAsync();
            var receiptWritten = false;
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var result = await CreateOnlyService(schema).ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "points.gpx",
                TableName = OverwriteGuardLogicalTable,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.CreateOnly,
                CommitParticipant = (_, _) => { receiptWritten = true; return ValueTask.CompletedTask; },
            });

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("require GeoJSON", "non-GeoJSON create-only is refused before staging");
            receiptWritten.Should().BeFalse();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeFalse();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
