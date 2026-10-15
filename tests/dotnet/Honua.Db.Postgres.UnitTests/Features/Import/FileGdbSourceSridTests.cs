// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using FluentAssertions;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// FileGDB source-CRS resolution (#6114). The fixture is a single point feature class written by
/// GDAL's OpenFileGDB driver with <c>-a_srs EPSG:2227</c>; the stored spatial reference is ESRI
/// WKT1 (<c>PROJCS["NAD_1983_StatePlane_California_III_FIPS_0403_Feet",...]</c>) with no
/// AUTHORITY node, exactly as ArcGIS writes it, and the coordinates are State Plane feet.
/// </summary>
public sealed class FileGdbSourceSridTests
{
    private const string StatePlaneName = "NAD_1983_StatePlane_California_III_FIPS_0403_Feet";

    private static readonly string FixturePath = Path.Join(
        AppContext.BaseDirectory, "Features", "Import", "Fixtures", "FileGdb", "stateplane-parcels.gdb.zip");

    [UnitTest]
    public async Task Reader_ProjectedEsriWktWithoutAuthority_LeavesSridUnknownInsteadOf4326()
    {
        using var extracted = ExtractedGdb.Create();

        var layers = FileGdbReader.DiscoverLayers(extracted.GdbPath);
        layers.Should().ContainSingle();
        layers[0].Srid.Should().Be(0, "the reader alone cannot resolve ESRI WKT without an AUTHORITY node");
        layers[0].SpatialReferenceWkt.Should().Contain(StatePlaneName,
            "the layer must expose its WKT so the import service can resolve it");

        var features = new List<IFeature>();
        await foreach (var feature in FileGdbReader.ReadStreamingAsync(extracted.GdbPath, CancellationToken.None))
        {
            features.Add(feature);
        }

        features.Should().HaveCount(2);
        features.Should().OnlyContain(feature => feature.Geometry.SRID == 0,
            "State Plane feet must never be stamped as EPSG:4326 degrees");
        features[0].Geometry.Coordinate.X.Should().BeApproximately(6010000.0, 0.01);
    }

    [UnitTest]
    public async Task Preview_ProjectedEsriWktWithoutAuthority_ResolvesThroughCrsDetectionService()
    {
        var crs = new StatePlaneCrsDetectionService();
        var service = CreateService(crs);

        await using var stream = File.OpenRead(FixturePath);
        var preview = await service.PreviewFileAsync(stream, "stateplane-parcels.gdb.zip");

        crs.PrjInputs.Should().ContainSingle().Which.Should().Contain(StatePlaneName,
            "FileGDB ESRI WKT must be resolved the same way as a shapefile .prj sidecar");
        preview.DetectedSrid.Should().Be(2227);
    }

    [UnitTest]
    public async Task Import_UnresolvableProjectedCrsWithoutSourceSrid_FailsWithSourceSridRequired()
    {
        // NoopCrsDetectionService resolves nothing: the import must refuse instead of
        // silently assuming TargetSrid (4326) for State Plane coordinates.
        var service = CreateService(new NoopCrsDetectionService());

        await using var stream = File.OpenRead(FixturePath);
        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "stateplane-parcels.gdb.zip",
            TableName = "stateplane_parcels",
            TargetSrid = 4326,
        });

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ImportValidationErrorCodes.SourceSridRequired, result.ErrorMessage);
    }

    private static StreamingFileImportService CreateService(ICrsDetectionService crs)
        => new(
            new ThrowingConnectionProvider(),
            crs,
            new TestFileFormatDetectionService(),
            new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance);

    /// <summary>
    /// Stands in for the Postgres-backed detector: resolves the State Plane WKT the way the
    /// spatial_ref_sys / ESRI-name lookup would, and records every .prj/WKT input it sees.
    /// </summary>
    private sealed class StatePlaneCrsDetectionService : ICrsDetectionService
    {
        public List<string> PrjInputs { get; } = [];

        public Task<int?> DetectFromPrjAsync(string prjContent, CancellationToken cancellationToken = default)
        {
            PrjInputs.Add(prjContent);
            return Task.FromResult<int?>(prjContent.Contains(StatePlaneName, StringComparison.Ordinal) ? 2227 : null);
        }

        public Task<int?> DetectFromWktAsync(string wktContent, CancellationToken cancellationToken = default)
            => DetectFromPrjAsync(wktContent, cancellationToken);

        public int? DetectFromEpsgCode(string epsgCode) => null;

        public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject, CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);

        public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath, CancellationToken cancellationToken = default)
            => Task.FromResult<int?>(null);

        public Task<bool> ValidateSridAsync(int srid, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class ExtractedGdb : IDisposable
    {
        private ExtractedGdb(string root) => Root = root;

        public string Root { get; }

        public string GdbPath => Path.Join(Root, "stateplane_parcels.gdb");

        public static ExtractedGdb Create()
        {
            var root = Path.Join(Path.GetTempPath(), "honua-6114-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(FixturePath, root);
            return new ExtractedGdb(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
