// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Compression;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;
using Honua.TestKit.Attributes;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

/// <summary>Produces real Honua artifacts for the independent GDAL reader in the required gate.</summary>
public sealed class IndependentVectorExportFixtureTests
{
    private const string Wgs84 = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433],AUTHORITY[\"EPSG\",\"4326\"]]";

    [UnitTheory]
    [InlineData("point", "POINT ZM (1 2 30 40)", "Point")]
    [InlineData("line", "MULTILINESTRING ZM ((1 2 30 40, 3 4 50 60), (5 6 70 80, 7 8 90 100))", "MultiLineString")]
    [InlineData("polygon", "POLYGON Z ((0 0 10, 4 0 10, 4 4 10, 0 4 10, 0 0 10), (1 1 10, 1 2 10, 2 2 10, 2 1 10, 1 1 10))", "Polygon")]
    public async Task WriteAllFormats_ForIndependentReader(string name, string wkt, string geometryType)
    {
        var retainedRoot = Environment.GetEnvironmentVariable("HONUA_VECTOR_PROOF_DIR");
        var root = retainedRoot ?? Path.Join(Path.GetTempPath(), $"honua-vector-proof-{Guid.NewGuid():N}");
        var directory = Path.Join(root, name);
        Directory.CreateDirectory(directory);
        try
        {
            var geometry = new WKTReader().Read(wkt);
            // The polygon fixture is XYZ, while point/line carry real measures.
            // Setting emitM for XYZ would manufacture an extra NaN M dimension.
            var wkb = new WKBWriter(ByteOrder.LittleEndian, false, true, name != "polygon").Write(geometry);
            var attributes = ImmutableDictionary<string, object?>.Empty
                .Add("label", "alpha, \"quoted\"").Add("amount", 7.25).Add("missing", null);
            Feature[] features = [Feature.Create(1, wkb, attributes), Feature.Create(2, null,
                attributes.SetItem("label", "unlocated").SetItem("amount", 0d))];
            ExportField[] fields = [new("label", ExportFieldType.String, true),
                new("amount", ExportFieldType.Double, true), new("missing", ExportFieldType.String, true)];
            await using (var csv = File.Create(Path.Join(directory, "export.csv")))
            {
                Assert.Equal(2, await CsvExportWriter.WriteAsync(csv, Rows(features), fields, CancellationToken.None));
            }
            await using (var zip = new MemoryStream())
            {
                var result = await ShapefileExportWriter.WriteAsync(zip, Rows(features), fields,
                    Enum.Parse<ExportGeometryType>(geometryType), Wgs84, NullLogger.Instance, CancellationToken.None);
                Assert.Equal(2, result.WrittenCount);
                zip.Position = 0;
                using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
                archive.ExtractToDirectory(directory);
            }
            Assert.Equal(2, await GeoPackageExportWriter.WriteAsync(Path.Join(directory, "export.gpkg"),
                Rows(features), fields, Enum.Parse<ExportGeometryType>(geometryType), 4326, "EPSG:4326", Wgs84,
                CancellationToken.None));
        }
        finally
        {
            if (retainedRoot is null)
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async IAsyncEnumerable<Feature> Rows(IEnumerable<Feature> features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }
        await Task.CompletedTask;
    }
}
