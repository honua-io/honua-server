// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Postgres.Features.GeoETL.Services.Connectors;

/// <summary>
/// Phase 1 GeoPackage source connector. Reads a <c>.gpkg</c> file through the managed
/// <c>Microsoft.Data.Sqlite</c> + <c>NetTopologySuite.IO.GeoPackage</c> path — the same
/// managed libraries the <c>StreamingFileImportService</c> GeoPackage import path uses —
/// so it carries no GDAL/OGR dependency and runs inside the lean serving image. The
/// connector lives in <c>Honua.Postgres</c> because that is where those managed readers
/// are referenced.
/// </summary>
/// <remarks>
/// Required <see cref="ConnectorConfig.Options"/>: <c>path</c> — absolute path to the
/// <c>.gpkg</c> file. Optional <c>layer</c> — the feature table to read; required when
/// the GeoPackage contains more than one feature layer, otherwise the single layer is
/// used automatically. Features stream one row at a time so memory stays constant.
/// </remarks>
public sealed partial class GeoPackageSourceConnector : IPipelineSourceConnector
{
    /// <summary>
    /// The connector type discriminator.
    /// </summary>
    public const string ConnectorType = "geopackage";

    /// <inheritdoc />
    public string Type => ConnectorType;

    /// <inheritdoc />
    public ConnectorRuntimeProfile RuntimeProfile => ConnectorRuntimeProfile.Managed;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> ReadAsync(
        ConnectorConfig config,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.Options.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "GeoPackage source connector requires a 'path' option pointing at the .gpkg file.");
        }

        config.Options.TryGetValue("layer", out var requestedLayer);

        var layers = await GetLayersAsync(path, cancellationToken).ConfigureAwait(false);
        var layer = ResolveLayer(layers, requestedLayer);

        await foreach (var feature in ReadLayerAsync(path, layer, cancellationToken).ConfigureAwait(false))
        {
            yield return feature;
        }
    }

    private static GeoPackageLayer ResolveLayer(IReadOnlyList<GeoPackageLayer> layers, string? requested)
    {
        if (layers.Count == 0)
        {
            throw new InvalidDataException("GeoPackage does not contain any feature layers.");
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            return layers.FirstOrDefault(l => string.Equals(l.TableName, requested, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"GeoPackage does not contain a feature layer named '{requested}'. " +
                    $"Available: {string.Join(", ", layers.Select(l => l.TableName))}.");
        }

        if (layers.Count > 1)
        {
            throw new InvalidDataException(
                "GeoPackage contains multiple feature layers " +
                $"({string.Join(", ", layers.Select(l => l.TableName))}). " +
                "Specify a 'layer' option to select one.");
        }

        return layers[0];
    }

    private static async Task<IReadOnlyList<GeoPackageLayer>> GetLayersAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly;");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT c.table_name, g.column_name, g.srs_id
            FROM gpkg_contents c
            JOIN gpkg_geometry_columns g ON c.table_name = g.table_name
            WHERE c.data_type = 'features'
            ORDER BY c.table_name
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var layers = new List<GeoPackageLayer>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0);
            var geometryColumn = reader.GetString(1);
            var srid = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            layers.Add(new GeoPackageLayer(tableName, geometryColumn, srid is > 0 ? srid : null));
        }

        return layers;
    }

    private static async IAsyncEnumerable<IFeature> ReadLayerAsync(
        string filePath,
        GeoPackageLayer layer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TableNameRegex().IsMatch(layer.TableName))
        {
            throw new InvalidOperationException("GeoPackage contains table name with unsupported characters.");
        }

        await using var connection = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly;");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{layer.TableName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var geometryOrdinal = reader.GetOrdinal(layer.GeometryColumn);
        var geoReader = new GeoPackageGeoReader
        {
            HandleSRID = true,
            RepairRings = true
        };

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            NtsGeometry? geometry = null;
            if (!reader.IsDBNull(geometryOrdinal))
            {
                var blob = reader.GetFieldValue<byte[]>(geometryOrdinal);
                geometry = geoReader.Read(blob);
                if (geometry is not null && layer.Srid.HasValue && geometry.SRID <= 0)
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex TableNameRegex();

    private sealed record GeoPackageLayer(string TableName, string GeometryColumn, int? Srid);
}
