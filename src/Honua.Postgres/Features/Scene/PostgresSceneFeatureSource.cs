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

        // Align with the canonical Postgres feature storage contract: every
        // Honua feature lives in the shared `features` table (objectid PK,
        // layer_id discriminator, geometry, attributes JSONB). The catalog's
        // per-layer StorageMapping advertises this table for every Postgres
        // layer; we read attributes from the JSONB column rather than as
        // physical columns, and we constrain the stream to the requested
        // layer.
        var qualifiedTable = DatabaseSchema.GetFeaturesTableName(layer.StorageMapping?.SchemaName);
        var attributeFields = ResolveAttributeFields(layer, includeAttributes);

        // ST_Force3D was previously wrapped around the geometry to ensure the
        // GLB writer always saw a Z component, but it masked the documented
        // "no Z and no extrusion" warning by promoting 2D rows to Z=0
        // silently. The WKB reader respects the geometry's hasZ flag, and the
        // GLB writer treats null vertex.Height as 0, so dropping the wrapper
        // preserves dimensionality without losing the writer's contract.
        var selectColumns = new StringBuilder();
        selectColumns.Append(DatabaseSchema.ObjectIdColumn).Append(" AS pk, ");
        selectColumns.Append("ST_AsBinary(ST_Transform(").Append(DatabaseSchema.GeometryColumn).Append(", 4326)) AS geom_wkb");
        for (var i = 0; i < attributeFields.Count; i++)
        {
            selectColumns.Append(", ").Append(DatabaseSchema.AttributesColumn)
                .Append(" ->> @attr_").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(" AS \"a_").Append(i.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        var sql = $"""
            SELECT {selectColumns}
            FROM {qualifiedTable}
            WHERE {DatabaseSchema.LayerIdColumn} = @layer_id
              AND {DatabaseSchema.ObjectIdColumn} > @last_key
            ORDER BY {DatabaseSchema.ObjectIdColumn} ASC
            LIMIT {PageSize}
            """;

        var lastKey = long.MinValue;
        var hasMore = true;

        while (hasMore)
        {
            // Throw on cancellation between batches so a cancelled
            // generation does not silently complete with partial data.
            // The previous loop condition `!cancellationToken.IsCancellationRequested`
            // exited normally on cancellation, which let the executor
            // treat a partial feature list as complete.
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await ReadBatchAsync(sql, layer.Id, lastKey, attributeFields, cancellationToken).ConfigureAwait(false);
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
        int layerId,
        long lastKey,
        List<FieldDefinition> attributeFields,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("@layer_id", layerId));
        command.Parameters.Add(new NpgsqlParameter("@last_key", lastKey));
        // Pass attribute field names as JSON-path parameters so the SQL itself
        // never interpolates field names (defense in depth — fields come from
        // the trusted catalog, but parameter binding keeps the contract
        // explicit and avoids surprises if a field name ever escapes the
        // existing catalog validation).
        for (var i = 0; i < attributeFields.Count; i++)
        {
            command.Parameters.Add(new NpgsqlParameter(
                $"@attr_{i.ToString(CultureInfo.InvariantCulture)}",
                attributeFields[i].Name));
        }

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
                var fieldName = attributeFields[i].Name;
                if (IsObjectIdAlias(fieldName))
                {
                    // The shared features table stores the row identifier in
                    // the objectid column, NOT in the attributes JSONB bag.
                    // Reading attributes ->> 'objectid' returns null whenever
                    // the JSONB does not duplicate the PK, which silently
                    // wrote 0 into the GLB metadata column for identify
                    // workflows. Project from the primary key so the
                    // generated metadata always carries the source row id.
                    attributes[fieldName] = pk;
                    continue;
                }

                // attributes ->> @field returns TEXT; the GLB writer's
                // ToInt32/ToInt64/ToFloat coercions parse string values
                // back to the declared SCHEMA/COMPONENT type, so handing
                // raw JSON text through is enough to round-trip integer,
                // float, and string columns from the JSONB store.
                var raw = reader.IsDBNull(2 + i) ? null : reader.GetValue(2 + i);
                attributes[fieldName] = raw;
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

    /// <summary>
    /// Returns true when the supplied field name is one of the canonical
    /// row-identifier aliases. Honua's shared <c>features</c> table stores
    /// the identifier in the <c>objectid</c> column rather than in the
    /// JSONB attributes bag, so the source must populate these slots from
    /// the primary key value.
    /// </summary>
    private static bool IsObjectIdAlias(string name)
        => string.Equals(name, "objectid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "object_id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "id", StringComparison.OrdinalIgnoreCase);
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

        // Two encodings carry higher-dimension geometry:
        //  * EWKB (PostGIS) stores Z/M/SRID as high bits of the type word:
        //      0x80000000 = Z, 0x40000000 = M, 0x20000000 = SRID prefix.
        //    `ST_AsBinary` strips the SRID prefix; Z and M may still ride
        //    along on layers declared with those dimensions.
        //  * ISO SQL/MM (OGC) uses the higher type ranges:
        //      1xxx = XYZ, 2xxx = XYM, 3xxx = XYZM.
        //
        // Previously the reader detected hasZ for both encodings but skipped
        // hasM entirely, so a LineStringM or PolygonZM stream left the M
        // ordinate in the buffer and the next vertex's "X" silently read M
        // from the prior vertex — corrupting the geometry without warning.
        var hasZ = (type & 0x80000000u) != 0;
        var hasM = (type & 0x40000000u) != 0;
        var baseType = type & 0xFFFFu;
        if (baseType >= 1000u && baseType < 4000u)
        {
            switch (baseType / 1000u)
            {
                case 1u: hasZ = true; break;
                case 2u: hasM = true; break;
                case 3u: hasZ = true; hasM = true; break;
            }
            baseType %= 1000u;
        }

        return baseType switch
        {
            1 => ReadPoint(ref cursor, byteOrder, hasZ, hasM),
            2 => ReadLineString(ref cursor, byteOrder, hasZ, hasM),
            3 => ReadPolygon(ref cursor, byteOrder, hasZ, hasM),
            4 => ReadMultiPoint(ref cursor, byteOrder),
            5 => ReadMultiLineString(ref cursor, byteOrder),
            6 => ReadMultiPolygon(ref cursor, byteOrder),
            _ => null
        };
    }

    private static SceneFeatureGeometry ReadPoint(ref WkbCursor cursor, byte byteOrder, bool hasZ, bool hasM)
    {
        var x = cursor.ReadDouble(byteOrder);
        var y = cursor.ReadDouble(byteOrder);
        double? z = hasZ ? cursor.ReadDouble(byteOrder) : null;
        // M is irrelevant to 3D Tiles output but must still be consumed so
        // any following geometry's first ordinate does not slide into the
        // unread M bytes.
        if (hasM)
        {
            cursor.ReadDouble(byteOrder);
        }
        return new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.Point,
            Vertices = new[] { new SceneVertex(x, y, z) }
        };
    }

    private static SceneFeatureGeometry ReadLineString(ref WkbCursor cursor, byte byteOrder, bool hasZ, bool hasM)
    {
        var count = cursor.ReadUInt32(byteOrder);
        var vertices = new SceneVertex[count];
        for (var i = 0; i < count; i++)
        {
            var x = cursor.ReadDouble(byteOrder);
            var y = cursor.ReadDouble(byteOrder);
            double? z = hasZ ? cursor.ReadDouble(byteOrder) : null;
            if (hasM)
            {
                cursor.ReadDouble(byteOrder);
            }
            vertices[i] = new SceneVertex(x, y, z);
        }
        return new SceneFeatureGeometry
        {
            Kind = SceneGeometryKind.LineString,
            Vertices = vertices
        };
    }

    private static SceneFeatureGeometry ReadPolygon(ref WkbCursor cursor, byte byteOrder, bool hasZ, bool hasM)
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
                if (hasM)
                {
                    cursor.ReadDouble(byteOrder);
                }
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

