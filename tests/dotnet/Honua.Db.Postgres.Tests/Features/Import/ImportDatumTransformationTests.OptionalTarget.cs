// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class ImportDatumTransformationTests
{
    public enum DatumCatalogKind
    {
        None,
        Curated
    }

    [IntegrationTheory]
    [InlineData(DatumCatalogKind.None)]
    [InlineData(DatumCatalogKind.Curated)]
    public async Task ImportFileAsync_WithoutTargetSrid_PreservesSourceSrid(DatumCatalogKind catalogKind)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();
            var service = CreateService(schema, catalogKind == DatumCatalogKind.Curated ? GeoServicesDatumTransformationCatalog.Create() : null);

            var result = await ImportPointWithoutTargetAsync(service, schema, ImportLoadMode.Replace);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);
            var (x, y, srid) = await ReadSinglePointAsync(schema, "imported_datum_optional");
            srid.Should().Be(4269);
            x.Should().BeApproximately(Nad83Lon, 1e-5);
            y.Should().BeApproximately(Nad83Lat, 1e-5);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData(DatumCatalogKind.None)]
    [InlineData(DatumCatalogKind.Curated)]
    public async Task ImportFileAsync_AppendWithoutTargetSrid_UsesExistingStorageSrid(DatumCatalogKind catalogKind)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportDatumTransformationTests));
        try
        {
            await EnsureImportFunctionsAsync();
            var service = CreateService(schema, catalogKind == DatumCatalogKind.Curated ? GeoServicesDatumTransformationCatalog.Create() : null);
            var initial = await ImportPointAsync(service, schema, "datum_optional", sourceSrid: 4326, targetSrid: 4326);
            initial.Success.Should().BeTrue(initial.ErrorMessage);

            var result = await ImportPointWithoutTargetAsync(service, schema, ImportLoadMode.Append);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(1);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*), MIN(ST_SRID(geometry)), MAX(ST_SRID(geometry)) FROM \"{schema}\".\"imported_datum_optional\";";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(2);
            reader.GetInt32(1).Should().Be(4326);
            reader.GetInt32(2).Should().Be(4326);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task<ImportResult> ImportPointWithoutTargetAsync(
        StreamingFileImportService service, string schema, ImportLoadMode loadMode)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
        return await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "points.geojson",
            TableName = "datum_optional",
            TargetSchema = schema,
            SourceSrid = 4269,
            LoadMode = loadMode
        });
    }

}
