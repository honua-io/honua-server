// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

public sealed partial class DirectImportCrsContractTests
{
    [IntegrationTest]
    public async Task ImportFile_NonSeekableLateGeoJsonCrs_KeepsSource()
    {
        var json = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[1740000,5420000]},\"properties\":{\"padding\":\""
            + new string('x', 20000) + "\"}}],\"crs\":{\"type\":\"name\",\"properties\":{\"name\":\"EPSG:2193\"}}}";
        await using var stream = new NonSeekableStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        await using var scope = _fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IFileImportService>().ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "late.geojson",
            TableName = $"late_crs_{Guid.NewGuid():N}",
            OverwriteExisting = true
        });
        if (result.Success) _importedTables.Add((result.Schema!, result.PhysicalTableName!));
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.DetectedSrid.Should().Be(2193);
        result.StoredSrid.Should().Be(2193);
    }

    [IntegrationTest]
    public async Task ImportFile_CsvMappedCoordinates_ValidatesSelectedGeometryBeforeCreatingTable()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("lon,lat,wkt\n174,-41,SRID=2193;POINT (1740000 5420000)\n"));
        await using var scope = _fixture.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IFileImportService>().ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "mapped.csv",
            TableName = $"mapped_crs_{Guid.NewGuid():N}",
            CsvOptions = new CsvImportOptions { LongitudeColumn = "lon", LatitudeColumn = "lat" }
        });
        if (result.Success) _importedTables.Add((result.Schema!, result.PhysicalTableName!));
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ImportValidationErrorCodes.SourceSridHeterogeneous);
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviewFile_Shapefile_ReportsPrjDeclaration(bool includePrj)
    {
        var (payload, _) = ShapefileImportTestHelpers.GetShapefileZipPayload();
        using var input = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
        await using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                if (!includePrj && entry.FullName.EndsWith(".prj", StringComparison.OrdinalIgnoreCase)) continue;
                using var source = entry.Open();
                using var target = zip.CreateEntry(entry.FullName).Open();
                await source.CopyToAsync(target);
            }
        }
        output.Position = 0;
        await using var scope = _fixture.Services.CreateAsyncScope();
        var preview = await scope.ServiceProvider.GetRequiredService<IFileImportService>().PreviewFileAsync(output, "sample.zip");
        preview.CoordinateSystem!.SourceCrsDeclared.Should().Be(includePrj);
        preview.CoordinateSystem.DetectedSrid.Should().Be(includePrj ? 4326 : null);
        preview.CoordinateSystem.RequiresSourceOverride.Should().Be(!includePrj);
    }
}
