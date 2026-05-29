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

        const string sql = """
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
            var srid = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            layers.Add(new GeoPackageLayerInfo(tableName, geometryColumn, NormalizeGeoPackageSrid(srid)));
        }

        return layers;
    }

    private static int? NormalizeGeoPackageSrid(int? srid)
    {
        if (!srid.HasValue)
        {
            return null;
        }

        return srid.Value <= 0 ? null : srid.Value;
    }
}
