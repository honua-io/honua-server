// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class ImportDatumTransformationTests
{
    [IntegrationTheory]
    [InlineData("108190", 4269)]
    [InlineData("108282", 4152)]
    [InlineData("108354", 6318)]
    [InlineData("Honua:WGS84_ITRF00_To_NAD83_NSRS2007_Via_2011", 4759)]
    public async Task ImportFileAsync_ExplicitRealization_MatchesIndependentReference(string operation, int sourceSrid)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();
            var service = CreateService(schema, GeoServicesDatumTransformationCatalog.Create());
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
                """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-96,41]},"properties":{}}]}"""));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "realization.geojson",
                TableName = "realization",
                TargetSchema = schema,
                SourceSrid = sourceSrid,
                TargetSrid = 4326,
                DatumTransformationWkid = int.TryParse(operation, out var wkid) ? wkid : null,
                DatumTransformationName = int.TryParse(operation, out _) ? null : operation
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            var point = await ReadSinglePointAsync(schema, "imported_realization");
            point.Srid.Should().Be(4326);
            point.X.Should().BeApproximately(-96.00000801053693, 8e-8);
            point.Y.Should().BeApproximately(41.00000720698309, 8e-8);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_TimedOperationWithEpoch_MatchesIndependentReference()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();
            var service = CreateService(schema, GeoServicesDatumTransformationCatalog.Create());
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
                """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-96,41,0]},"properties":{}}]}"""));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "realization.geojson",
                TableName = "realization",
                TargetSchema = schema,
                SourceSrid = 7912,
                TargetSrid = 6319,
                DatumTransformationWkid = 108574,
                CoordinateEpoch = 2020
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            var point = await ReadSinglePointAsync(schema, "imported_realization");
            point.Srid.Should().Be(6319);
            point.X.Should().BeApproximately(-95.99998731925608, 8e-8);
            point.Y.Should().BeApproximately(40.999993417501, 8e-8);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_TimedOperationWithoutEpoch_RejectsBeforeWriting()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();
            var service = CreateService(schema, GeoServicesDatumTransformationCatalog.Create());
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "realization.geojson",
                TableName = "realization",
                TargetSchema = schema,
                SourceSrid = 7912,
                TargetSrid = 6319,
                DatumTransformationWkid = 108574
            });

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("coordinateEpoch");
            result.FeatureCount.Should().Be(0);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
