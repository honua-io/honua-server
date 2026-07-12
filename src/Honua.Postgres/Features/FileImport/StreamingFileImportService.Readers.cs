// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    /// <summary>
    /// Stream Shapefile features from extracted components on disk.
    /// </summary>
    private static async IAsyncEnumerable<IFeature> ReadShapefileStreamingAsync(
        string shapefilePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = new ShapefileReaderOptions
        {
            GeometryBuilderMode = GeometryBuilderMode.QuickFixInvalidShapes
        };

        using var reader = Shapefile.OpenRead(shapefilePath, options);
        var recordIndex = 0;

        while (reader.Read(out var deleted, out var feature))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (deleted || feature == null)
            {
                continue;
            }

            yield return feature;

            if (++recordIndex % 256 == 0)
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Stream GeoPackage features from a stream by using a temporary SQLite file.
    /// </summary>
    private async IAsyncEnumerable<IFeature> ReadGeoPackageStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GeoPackageScratch? scratch = null;
        var filePath = (stream as FileStream)?.Name;
        var ownsScratch = false;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            scratch = await PrepareGeoPackageScratchAsync(stream, cancellationToken);
            filePath = scratch.FilePath;
            ownsScratch = true;
        }

        try
        {
            await foreach (var feature in ReadGeoPackageStreamingAsync(filePath, cancellationToken))
            {
                yield return feature;
            }
        }
        finally
        {
            if (ownsScratch)
            {
                CleanupGeoPackageScratch(scratch);
            }
        }
    }

    private static async IAsyncEnumerable<IFeature> ReadGeoPackageStreamingAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layers = await GetGeoPackageLayersAsync(filePath, cancellationToken);
        var layer = ResolveSingleGeoPackageImportLayer(layers);
        await foreach (var feature in ReadGeoPackageLayerAsync(filePath, layer, cancellationToken))
        {
            yield return feature;
        }
    }

    private static GeoPackageLayerInfo ResolveSingleGeoPackageImportLayer(IReadOnlyList<GeoPackageLayerInfo> layers)
    {
        if (layers.Count == 0)
        {
            throw new InvalidDataException("GeoPackage does not contain any feature layers.");
        }

        if (layers.Count > 1)
        {
            throw new InvalidDataException(BuildMultiLayerGeoPackageImportMessage(layers));
        }

        return layers[0];
    }

    private static string BuildMultiLayerGeoPackageImportMessage(IReadOnlyList<GeoPackageLayerInfo> layers)
    {
        var layerNames = string.Join(", ", layers.Select(layer => layer.TableName));
        return $"GeoPackage contains multiple feature layers ({layerNames}). Import requires a single-layer GeoPackage; preview AvailableLayers lists the source layers to export or split before import.";
    }

    private static async IAsyncEnumerable<IFeature> ReadGeoPackageLayerAsync(
        string filePath,
        GeoPackageLayerInfo layer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!GeoPackageTableNameRegex().IsMatch(layer.TableName))
        {
            throw new InvalidOperationException("GeoPackage contains table name with unsupported characters.");
        }

        await using var connection = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly;");
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(layer.TableName)}";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var geometryOrdinal = reader.GetOrdinal(layer.GeometryColumn);
        var geoReader = new GeoPackageGeoReader
        {
            HandleSRID = true,
            RepairRings = true
        };

        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            NtsGeometry? geometry = null;
            if (!reader.IsDBNull(geometryOrdinal))
            {
                var blob = reader.GetFieldValue<byte[]>(geometryOrdinal);
                geometry = geoReader.Read(blob);
                if (geometry != null && layer.Srid.HasValue && geometry.SRID <= 0)
                {
                    geometry.SRID = layer.Srid.Value;
                }
            }

            var attributes = new AttributesTable();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i == geometryOrdinal)
                {
                    continue;
                }

                var name = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                attributes.Add(name, value);
            }

            yield return new Feature(geometry, attributes);
        }
    }

    private static async Task<IReadOnlyList<GeoPackageLayerInfo>> GetGeoPackageLayersAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly;");
        await connection.OpenAsync(cancellationToken);

        // Resolve the layer's srs_id through gpkg_spatial_ref_sys rather than trusting srs_id to be
        // an EPSG code directly (#2743). The GeoPackage spec allows local srs_id numbering: a file
        // may declare srs_id=1 whose row maps to organization='EPSG', organization_coordsys_id=27700.
        // Reading srs_id as the EPSG code would silently mis-georeference every feature.
        //
        // gpkg_spatial_ref_sys is required by the spec, but malformed files omit it. Joining against
        // a missing table throws "no such table"; probe first and fall back to a join-less query that
        // reads the raw srs_id (best effort) rather than failing the whole import. The raw srs_id
        // still passes through ResolveGeoPackageSrid and downstream ValidateImportSridsAsync, which
        // reject nonsense codes.
        var hasSpatialRefSys = await GeoPackageSpatialRefSysTableExistsAsync(connection, cancellationToken);

        var sql = hasSpatialRefSys
            ? """
                SELECT c.table_name, g.column_name, g.srs_id, s.organization, s.organization_coordsys_id
                FROM gpkg_contents c
                JOIN gpkg_geometry_columns g ON c.table_name = g.table_name
                LEFT JOIN gpkg_spatial_ref_sys s ON g.srs_id = s.srs_id
                WHERE c.data_type = 'features'
                ORDER BY c.table_name
                """
            : """
                SELECT c.table_name, g.column_name, g.srs_id
                FROM gpkg_contents c
                JOIN gpkg_geometry_columns g ON c.table_name = g.table_name
                WHERE c.data_type = 'features'
                ORDER BY c.table_name
                """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var layers = new List<GeoPackageLayerInfo>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var tableName = reader.GetString(0);
            var geometryColumn = reader.GetString(1);
            var srsId = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            var organization = hasSpatialRefSys && !reader.IsDBNull(3) ? reader.GetString(3) : null;
            var organizationCoordSysId = hasSpatialRefSys && !reader.IsDBNull(4) ? reader.GetInt32(4) : (int?)null;
            layers.Add(new GeoPackageLayerInfo(
                tableName,
                geometryColumn,
                ResolveGeoPackageSrid(srsId, organization, organizationCoordSysId)));
        }

        return layers;
    }

    private static async Task<bool> GeoPackageSpatialRefSysTableExistsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'gpkg_spatial_ref_sys' LIMIT 1";
        var result = await probe.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    /// <summary>
    /// Resolves a GeoPackage geometry-column <c>srs_id</c> to an EPSG code via its
    /// <c>gpkg_spatial_ref_sys</c> row (#2743). Honors the spec's reserved ids
    /// (<c>0</c> undefined-geographic, <c>-1</c> undefined-cartesian → undetected) and only
    /// trusts <c>organization_coordsys_id</c> for EPSG-authored rows. A non-EPSG organization
    /// (or a row that resolves to a non-positive code) is treated as undetected so the caller
    /// must supply an explicit source SRID rather than the import guessing a wrong EPSG code.
    /// Falls back to the raw <c>srs_id</c> only when no matching <c>gpkg_spatial_ref_sys</c> row
    /// exists (best effort for a malformed file that still numbers by EPSG).
    /// </summary>
    private static int? ResolveGeoPackageSrid(int? srsId, string? organization, int? organizationCoordSysId)
    {
        if (!srsId.HasValue)
        {
            return null;
        }

        // Reserved srs_ids per the GeoPackage spec: 0 (undefined geographic) and -1 (undefined
        // cartesian). Both mean "no real CRS", so they are undetected.
        if (srsId.Value is 0 or (-1))
        {
            return null;
        }

        if (organization is not null)
        {
            if (string.Equals(organization, "EPSG", StringComparison.OrdinalIgnoreCase))
            {
                return organizationCoordSysId is > 0 ? organizationCoordSysId : null;
            }

            // Known srs row, but a non-EPSG authority (e.g. 'NONE', a vendor org, or a fully
            // custom WKT-only definition). Do not guess an EPSG code — require an explicit SRID.
            return null;
        }

        // No gpkg_spatial_ref_sys row matched srs_id. Fall back to the raw value on the chance the
        // file numbers by EPSG directly (legacy/malformed writers).
        return srsId.Value > 0 ? srsId.Value : null;
    }
}
