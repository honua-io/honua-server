// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed partial class BoundedGeometryReaderGuardTests
{
    private static readonly HashSet<string> ApprovedBoundedHelpers = new(StringComparer.Ordinal)
    {
        "src/Honua.Geometry/Queries/Filters/FilterParserGeometryGuard.cs",
        "src/Honua.Hosting/Features/Services/GeometryService.cs",
    };

    private static readonly HashSet<string> LegacyBaseline = new(StringComparer.Ordinal)
    {
        "src/Honua.Db/Postgres/Features/FeatureStore/Services/FeatureDataAccess.Edits.cs",
        "src/Honua.Db/Postgres/Features/GeometryService/PostgresGeometryOperationService.cs",
        "src/Honua.Db/Postgres/Features/Geoprocessing/HonuaLayerDagSource.cs",
        "src/Honua.Db/SqlServer/Features/FeatureStore/Services/SqlServerGeographyWinding.cs",
        "src/Honua.Geometry/Features/Authorization/RlsPolicyRestrictions.cs",
        "src/Honua.Geometry/Features/FeatureExport/FlatGeobufFeatureWriter.cs",
        "src/Honua.Geometry/Features/FileImport/Services/CsvFormatReader.cs",
        "src/Honua.Geometry/Features/FileImport/Services/GeoParquetReader.cs",
        "src/Honua.Geometry/Features/FileImport/Services/WkbFormatReader.cs",
        "src/Honua.Geometry/Features/FileImport/Services/WktFormatReader.cs",
        "src/Honua.Geometry/Features/Spec/Canonical/SpecJsonReader.cs",
        "src/Honua.Geometry/Features/Spec/Grammar/SpecParser.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/CsvSourceExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/GeometryBufferJobExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/GeometryDissolveJobExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/GeometryExecutorSupport.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/GeometryFormatConvertJobExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/GeometryProjectJobExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/GeometrySimplifyJobExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/GeometrySnapJobExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/GeometryUnionJobExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/LayerBufferAggregateExecutor.cs",
        "src/Honua.Geoprocessing/Core/Features/Geoprocessing/Execution/SpatialFilterTransformExecutor.cs",
        "src/Honua.Hosting/Features/Events/WkbReaderCache.cs",
        "src/Honua.Hosting/Features/Filtering/Cql2FilterProcessor.cs",
        "src/Honua.Hosting/Features/Services/GeoParquetFeatureWriter.cs",
        "src/Honua.Hosting/Features/Validation/FeatureMutationValidator.cs",
        "src/Honua.Hosting/Features/Validation/GeographicBoundsValidator.cs",
        "src/Honua.Protocols/GeoServices/GPServer/GPServerEsriInputTranslation.cs",
        "src/Honua.Protocols/GeoServices/Geometry/GeometryConverter.cs",
        "src/Honua.Protocols/GeoServices/GeometryService/Services/GeometryServiceHandler.cs",
        "src/Honua.Protocols/GeoServices/GeometryService/Services/GeometryServiceOverlayBudget.cs",
        "src/Honua.Protocols/GeoServices/MapServer/MapServerRequestHandlers.GenerateKml.cs",
        "src/Honua.Protocols/GeoServices/MapServer/MapServerRequestHandlers.Identify.cs",
        "src/Honua.Protocols/Ogc.Shared/Common/OgcFeaturesGeometryServices.cs",
        "src/Honua.Protocols/OgcApi/Coverages/Handlers/OgcCoveragesHandler.Export.cs",
        "src/Honua.Protocols/OgcApi/Coverages/Handlers/OgcCoveragesHandler.cs",
        "src/Honua.Protocols/OgcClassic/Wfs20/Services/Wfs20Handler.Legacy.cs",
        "src/Honua.Server/Features/Admin/AlertAdminEndpoints.cs",
        "src/Honua.Server/Features/Admin/AlertZoneGeometryParser.cs",
        "src/Honua.Server/Features/Admin/Exports/ExportFeatureCapture.cs",
        "src/Honua.Server/Features/Alerts/DefaultAlertEvaluator.cs",
        "src/Honua.Server/Features/Protocols/Elevation/ElevationEndpoints.cs",
        "src/Honua.Server/Features/Protocols/Grpc/GrpcConversionHelpers.cs",
        "src/Honua.Worker.Gdal/Execution/GdalRasterClipJobExecutor.cs",
    };

    [ArchitectureTest]
    public void DirectWktOrWkbReaderConstruction_IsRestrictedToApprovedBoundedHelpers()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sourceRoot = ArchitectureTestHelpers.CombinePath(repoRoot, "src");

        var directReaderFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Where(file => ReaderConstructionPattern().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var unauthorized = directReaderFiles
            .Where(path => !ApprovedBoundedHelpers.Contains(path) && !LegacyBaseline.Contains(path))
            .ToArray();

        unauthorized.Should().BeEmpty(
            "direct WKTReader/WKBReader construction must go through a bounded helper that enforces nesting depth limits before parsing");

        var filterDirectReaders = directReaderFiles
            .Where(path => path.Contains("/Queries/Filters/", StringComparison.Ordinal)
                           && !ApprovedBoundedHelpers.Contains(path))
            .ToArray();

        filterDirectReaders.Should().BeEmpty(
            "all filter parsers must route WKT/WKB parsing through FilterParserGeometryGuard");
    }

    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [GeneratedRegex(@"\bnew\s+WK[TB]Reader\b|\bWK[TB]Reader\s+[^;]*=\s*new\s*\(")]
    private static partial Regex ReaderConstructionPattern();
}
