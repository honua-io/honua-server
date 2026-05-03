// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Scene;

/// <summary>
/// Postgres/PostGIS implementation of <see cref="ISceneFeatureSource"/>. Streams
/// the canonical 3D Tiles input shape (WGS-84 lon/lat plus optional Z) for a
/// catalog layer using <c>ST_AsBinary(ST_Transform(geom, 4326))</c>.
/// </summary>
internal sealed partial class PostgresSceneFeatureSource : ISceneFeatureSource
{
    private const int PageSize = 1_000;

    private readonly IPrimaryDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresSceneFeatureSource> _logger;

    public PostgresSceneFeatureSource(
        IPrimaryDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresSceneFeatureSource> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async IAsyncEnumerable<SceneFeature> StreamAsync(
        LayerDefinition layer,
        IReadOnlyList<string> includeAttributes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(includeAttributes);

        var mapping = layer.StorageMapping
            ?? throw new InvalidOperationException(
                $"Layer {layer.Id} has no storage mapping; cannot stream features for scene generation.");

        if (string.IsNullOrEmpty(mapping.GeometryColumn))
        {
            throw new InvalidOperationException(
                $"Layer {layer.Id} has no geometry column; cannot generate 3D Tiles.");
        }

        var quotedTable = QuoteIdentifier(mapping.TableName);
        var quotedSchema = string.IsNullOrEmpty(mapping.SchemaName)
            ? null
            : QuoteIdentifier(mapping.SchemaName!);
        var qualifiedTable = quotedSchema is null ? quotedTable : $"{quotedSchema}.{quotedTable}";
        var quotedKey = QuoteIdentifier(mapping.PrimaryKeyColumn);
        var quotedGeom = QuoteIdentifier(mapping.GeometryColumn!);

        var attributeFields = ResolveAttributeFields(layer, includeAttributes);
        var selectColumns = new StringBuilder();
        selectColumns.Append(quotedKey).Append(" AS pk, ");
        selectColumns.Append("ST_AsBinary(ST_Transform(ST_Force3D(").Append(quotedGeom).Append("), 4326)) AS geom_wkb");
        foreach (var field in attributeFields)
        {
            selectColumns.Append(", ").Append(QuoteIdentifier(field.Name)).Append(" AS ")
                .Append(QuoteIdentifier($"a_{field.Name}"));
        }

        var sql = $"""
            SELECT {selectColumns}
            FROM {qualifiedTable}
            WHERE {quotedKey} > @last_key
            ORDER BY {quotedKey} ASC
            LIMIT {PageSize}
            """;

        var lastKey = long.MinValue;
        var hasMore = true;

        while (hasMore && !cancellationToken.IsCancellationRequested)
        {
            var batch = await ReadBatchAsync(sql, lastKey, attributeFields, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                hasMore = false;
                break;
            }

            foreach (var feature in batch)
            {
                yield return feature;
            }

            lastKey = batch[^1].Id;
            hasMore = batch.Count == PageSize;
        }
    }

    private async Task<List<SceneFeature>> ReadBatchAsync(
        string sql,
        long lastKey,
        List<FieldDefinition> attributeFields,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("@last_key", lastKey));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<SceneFeature>(PageSize);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var pk = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            byte[]? wkb = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
            if (wkb is null)
            {
                continue;
            }

            var geometry = WkbGeometryReader.Parse(wkb);
            if (geometry is null)
            {
                continue;
            }

            var attributes = new Dictionary<string, object?>(attributeFields.Count, StringComparer.Ordinal);
            for (var i = 0; i < attributeFields.Count; i++)
            {
                var raw = reader.IsDBNull(2 + i) ? null : reader.GetValue(2 + i);
                attributes[attributeFields[i].Name] = raw;
            }

            results.Add(new SceneFeature
            {
                Id = pk,
                Geometry = geometry,
                Attributes = attributes
            });
        }

        return results;
    }

    private static List<FieldDefinition> ResolveAttributeFields(
        LayerDefinition layer,
        IReadOnlyList<string> includeAttributes)
    {
        var candidates = layer.AttributeFields
            .Where(f => f.Type is FieldType.Integer or FieldType.BigInteger or FieldType.Double
                or FieldType.Float or FieldType.String)
            .ToList();

        if (includeAttributes.Count == 0)
        {
            return candidates;
        }

        var allow = new HashSet<string>(includeAttributes, StringComparer.OrdinalIgnoreCase);
        return candidates.Where(f => allow.Contains(f.Name)).ToList();
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            throw new InvalidOperationException("Identifier may not be empty.");
        }
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>
/// Minimal WKB reader for the geometry kinds the v1 3D Tiles pipeline
/// accepts: Point, LineString, Polygon, and their Multi variants. Returns
/// the first ring/part for multi-geometries — v1 collapses multi inputs into
/// a single deterministic representation.
/// </summary>
internal static class WkbGeometryReader
{
    public static SceneFeatureGeometry? Parse(byte[] wkb)
    {
        if (wkb.Length < 5)
        {
            return null;
        }

        var cursor = new WkbCursor(wkb);
        return ReadGeometry(ref cursor);
    }

    private static SceneFeatureGeometry? ReadGeometry(ref WkbCursor cursor)
    {
        var byteOrder = cursor.ReadByte();
        var type = cursor.ReadUInt32(byteOrder);
        var hasZ = (type & 0x80000000u) != 0 || ((type / 1000u) is 1u or 3u);
        var baseType = type & 0xFFFFu;
        if (baseType > 1000)
        {
            baseType %= 1000u;
        }

        return baseType switch
        {
            1 => ReadPoint(ref cursor, byteOrder, hasZ),
            2 => ReadLineString(ref cursor, byteOrder, hasZ),
            3 => ReadPolygon(ref cursor, byteOrder, hasZ),
            4 => ReadMultiPoint(ref cursor, byteOrder),
            5 => ReadMultiLineString(ref cursor, byteOrder),
            6 => ReadMultiPolygon(ref cursor, byteOrder),
            _ => null
        };
    }

    private static SceneFeatureGeometry ReadPoint(ref WkbCursor cursor, byte byteOrder, bool hasZ)
    {
        var x = cursor.ReadDouble(byteOrder);
        var y = cursor.ReadDouble(byteOrder);
        double? z = hasZ ? cursor.ReadDouble(byteOrder) : null;
        return new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.Point,
            Vertices = new[] { new SceneVertex(x, y, z) }
        };
    }

    private static SceneFeatureGeometry ReadLineString(ref WkbCursor cursor, byte byteOrder, bool hasZ)
    {
        var count = cursor.ReadUInt32(byteOrder);
        var vertices = new SceneVertex[count];
        for (var i = 0; i < count; i++)
        {
            var x = cursor.ReadDouble(byteOrder);
            var y = cursor.ReadDouble(byteOrder);
            double? z = hasZ ? cursor.ReadDouble(byteOrder) : null;
            vertices[i] = new SceneVertex(x, y, z);
        }
        return new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.LineString,
            Vertices = vertices
        };
    }

    private static SceneFeatureGeometry ReadPolygon(ref WkbCursor cursor, byte byteOrder, bool hasZ)
    {
        var ringCount = cursor.ReadUInt32(byteOrder);
        SceneVertex[] outer = [];
        for (var r = 0; r < ringCount; r++)
        {
            var pointCount = cursor.ReadUInt32(byteOrder);
            var ring = new SceneVertex[pointCount];
            for (var i = 0; i < pointCount; i++)
            {
                var x = cursor.ReadDouble(byteOrder);
                var y = cursor.ReadDouble(byteOrder);
                double? z = hasZ ? cursor.ReadDouble(byteOrder) : null;
                ring[i] = new SceneVertex(x, y, z);
            }
            if (r == 0)
            {
                outer = ring;
            }
        }

        return new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.Polygon,
            Vertices = outer
        };
    }

    private static SceneFeatureGeometry? ReadMultiPoint(ref WkbCursor cursor, byte byteOrder)
    {
        var count = cursor.ReadUInt32(byteOrder);
        if (count == 0)
        {
            return null;
        }
        var first = ReadGeometry(ref cursor);
        return first;
    }

    private static SceneFeatureGeometry? ReadMultiLineString(ref WkbCursor cursor, byte byteOrder)
    {
        var count = cursor.ReadUInt32(byteOrder);
        if (count == 0)
        {
            return null;
        }
        return ReadGeometry(ref cursor);
    }

    private static SceneFeatureGeometry? ReadMultiPolygon(ref WkbCursor cursor, byte byteOrder)
    {
        var count = cursor.ReadUInt32(byteOrder);
        if (count == 0)
        {
            return null;
        }
        return ReadGeometry(ref cursor);
    }
}

internal ref struct WkbCursor
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public WkbCursor(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public byte ReadByte()
    {
        var b = _data[_position];
        _position += 1;
        return b;
    }

    public uint ReadUInt32(byte byteOrder)
    {
        var slice = _data.Slice(_position, 4);
        _position += 4;
        return byteOrder == 1
            ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
            : BinaryPrimitives.ReadUInt32BigEndian(slice);
    }

    public double ReadDouble(byte byteOrder)
    {
        var slice = _data.Slice(_position, 8);
        _position += 8;
        return byteOrder == 1
            ? BinaryPrimitives.ReadDoubleLittleEndian(slice)
            : BinaryPrimitives.ReadDoubleBigEndian(slice);
    }
}

