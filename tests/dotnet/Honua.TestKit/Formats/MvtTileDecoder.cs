// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;

namespace Honua.TestKit.Formats;

/// <summary>
/// A dependency-free decoder for Mapbox Vector Tile 2.1 payloads
/// (<see href="https://github.com/mapbox/vector-tile-spec/tree/master/2.1"/>), written for tests
/// that need to assert what a tile actually contains (honua-server#4421).
/// </summary>
/// <remarks>
/// <para>
/// Before this type no .NET test in the repository decoded an MVT: no decoder library was
/// referenced, no <c>vector_tile.proto</c> existed, and every
/// <c>application/vnd.mapbox-vector-tile</c> assertion checked a content-type header or that the
/// body was non-empty. A tile pipeline that clipped wrongly, ignored a <c>where=</c> filter,
/// dropped features at low zoom or emitted an undecodable payload passed all of them.
/// </para>
/// <para>
/// This is deliberately a decoder and not a dependency: the wire format is a small, frozen subset
/// of protobuf (varints, length-delimited fields, packed uint32 arrays), and a test asset that
/// re-implements the spec independently of the producer is worth more than one that shares the
/// producer's library. It is strict — a malformed payload throws
/// <see cref="InvalidDataException"/> rather than yielding a partial tile — because "the bytes did
/// not decode" is exactly the failure these tests exist to catch.
/// </para>
/// </remarks>
public static class MvtTileDecoder
{
    private const int TileLayersField = 3;

    private const int LayerNameField = 1;
    private const int LayerFeaturesField = 2;
    private const int LayerKeysField = 3;
    private const int LayerValuesField = 4;
    private const int LayerExtentField = 5;
    private const int LayerVersionField = 15;

    private const int FeatureIdField = 1;
    private const int FeatureTagsField = 2;
    private const int FeatureTypeField = 3;
    private const int FeatureGeometryField = 4;

    private const int MoveTo = 1;
    private const int LineTo = 2;
    private const int ClosePath = 7;

    /// <summary>Decodes a tile payload into its layers.</summary>
    /// <exception cref="InvalidDataException">The payload is not a well-formed vector tile.</exception>
    public static MvtTile Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new InvalidDataException("Vector tile payload is empty.");
        }

        var layers = new List<MvtLayer>();
        var reader = new ProtoReader(payload);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == TileLayersField && wireType == 2)
            {
                layers.Add(DecodeLayer(reader.ReadLengthDelimited()));
            }
            else
            {
                reader.SkipField(wireType);
            }
        }

        if (layers.Count == 0)
        {
            throw new InvalidDataException("Vector tile decoded successfully but declares no layers.");
        }

        return new MvtTile(layers);
    }

    /// <summary>Decodes a tile payload, returning <see langword="false"/> instead of throwing.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out MvtTile? tile)
    {
        try
        {
            tile = Decode(payload);
            return true;
        }
        catch (InvalidDataException)
        {
            tile = null;
            return false;
        }
    }

    private static MvtLayer DecodeLayer(ReadOnlySpan<byte> payload)
    {
        string? name = null;
        uint version = 0;
        uint extent = 4096;
        var keys = new List<string>();
        var values = new List<object?>();
        var featurePayloads = new List<byte[]>();

        var reader = new ProtoReader(payload);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case LayerNameField when wireType == 2:
                    name = Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                    break;
                case LayerFeaturesField when wireType == 2:
                    featurePayloads.Add(reader.ReadLengthDelimited().ToArray());
                    break;
                case LayerKeysField when wireType == 2:
                    keys.Add(Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                    break;
                case LayerValuesField when wireType == 2:
                    values.Add(DecodeValue(reader.ReadLengthDelimited()));
                    break;
                case LayerExtentField when wireType == 0:
                    extent = (uint)reader.ReadVarint();
                    break;
                case LayerVersionField when wireType == 0:
                    version = (uint)reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (name is null)
        {
            throw new InvalidDataException("Vector tile layer has no name.");
        }

        if (extent == 0)
        {
            throw new InvalidDataException($"Vector tile layer '{name}' declares a zero extent.");
        }

        var features = featurePayloads
            .Select(featurePayload => DecodeFeature(featurePayload, keys, values, name))
            .ToList();

        return new MvtLayer(name, version, extent, features);
    }

    private static MvtFeature DecodeFeature(
        ReadOnlySpan<byte> payload, List<string> keys, List<object?> values, string layerName)
    {
        ulong? id = null;
        var type = MvtGeometryType.Unknown;
        var tags = new List<uint>();
        var commands = new List<uint>();

        var reader = new ProtoReader(payload);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case FeatureIdField when wireType == 0:
                    id = reader.ReadVarint();
                    break;
                case FeatureTypeField when wireType == 0:
                    type = (MvtGeometryType)reader.ReadVarint();
                    break;
                case FeatureTagsField:
                    ReadPackedUInt32(ref reader, wireType, tags);
                    break;
                case FeatureGeometryField:
                    ReadPackedUInt32(ref reader, wireType, commands);
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (tags.Count % 2 != 0)
        {
            throw new InvalidDataException($"Layer '{layerName}' has a feature with an odd tag count.");
        }

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < tags.Count; i += 2)
        {
            var keyIndex = (int)tags[i];
            var valueIndex = (int)tags[i + 1];
            if (keyIndex >= keys.Count || valueIndex >= values.Count)
            {
                throw new InvalidDataException(
                    $"Layer '{layerName}' has a feature tag pointing outside the key/value dictionaries.");
            }

            attributes[keys[keyIndex]] = values[valueIndex];
        }

        return new MvtFeature(id, type, attributes, DecodeGeometry(commands, layerName));
    }

    /// <summary>
    /// Decodes the command/parameter integer stream into rings of tile-space coordinates. Point
    /// features yield one single-coordinate ring per point; line and polygon features yield one
    /// ring per part (polygon interior rings included, in wire order).
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<MvtPoint>> DecodeGeometry(List<uint> commands, string layerName)
    {
        var rings = new List<IReadOnlyList<MvtPoint>>();
        List<MvtPoint>? current = null;
        long x = 0;
        long y = 0;

        var index = 0;
        while (index < commands.Count)
        {
            var command = commands[index++];
            var id = (int)(command & 0x7);
            var count = (int)(command >> 3);

            switch (id)
            {
                case MoveTo:
                case LineTo:
                {
                    if (index + (count * 2) > commands.Count)
                    {
                        throw new InvalidDataException(
                            $"Layer '{layerName}' has a geometry command running past the end of the stream.");
                    }

                    for (var i = 0; i < count; i++)
                    {
                        x += ZigZag(commands[index++]);
                        y += ZigZag(commands[index++]);
                        if (id == MoveTo)
                        {
                            current = [];
                            rings.Add(current);
                        }

                        if (current is null)
                        {
                            throw new InvalidDataException(
                                $"Layer '{layerName}' has a LineTo before any MoveTo.");
                        }

                        current.Add(new MvtPoint(x, y));
                    }

                    break;
                }

                case ClosePath:
                {
                    if (current is null or { Count: 0 })
                    {
                        throw new InvalidDataException($"Layer '{layerName}' has a ClosePath with no open ring.");
                    }

                    // The spec omits the repeated closing vertex on the wire; materialize it so a
                    // caller can compare rings to source geometry directly.
                    current.Add(current[0]);
                    break;
                }

                default:
                    throw new InvalidDataException(
                        $"Layer '{layerName}' has an unknown geometry command id {id}.");
            }
        }

        return rings;
    }

    private static object? DecodeValue(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtoReader(payload);
        while (!reader.IsAtEnd)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1 when wireType == 2:
                    return Encoding.UTF8.GetString(reader.ReadLengthDelimited());
                case 2 when wireType == 5:
                    return BinaryPrimitives.ReadSingleLittleEndian(reader.ReadFixed(4));
                case 3 when wireType == 1:
                    return BinaryPrimitives.ReadDoubleLittleEndian(reader.ReadFixed(8));
                case 4 when wireType == 0:
                    return (long)reader.ReadVarint();
                case 5 when wireType == 0:
                    return reader.ReadVarint();
                case 6 when wireType == 0:
                    return (long)ZigZag64(reader.ReadVarint());
                case 7 when wireType == 0:
                    return reader.ReadVarint() != 0;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        return null;
    }

    private static void ReadPackedUInt32(ref ProtoReader reader, int wireType, List<uint> destination)
    {
        if (wireType == 0)
        {
            destination.Add((uint)reader.ReadVarint());
            return;
        }

        if (wireType != 2)
        {
            throw new InvalidDataException($"Unexpected wire type {wireType} for a packed uint32 field.");
        }

        var packed = new ProtoReader(reader.ReadLengthDelimited());
        while (!packed.IsAtEnd)
        {
            destination.Add((uint)packed.ReadVarint());
        }
    }

    private static long ZigZag(uint value) => (value >> 1) ^ (uint)-(value & 1);

    private static long ZigZag64(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>Minimal forward-only protobuf reader over a span.</summary>
    private ref struct ProtoReader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _position;

        public readonly bool IsAtEnd => _position >= _buffer.Length;

        public (int Field, int WireType) ReadTag()
        {
            var tag = ReadVarint();
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if (field == 0)
            {
                throw new InvalidDataException("Protobuf field number 0 is not valid.");
            }

            return (field, wireType);
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                if (_position >= _buffer.Length)
                {
                    throw new InvalidDataException("Protobuf varint runs past the end of the buffer.");
                }

                var current = _buffer[_position++];
                result |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
                if (shift > 63)
                {
                    throw new InvalidDataException("Protobuf varint is longer than 10 bytes.");
                }
            }
        }

        public ReadOnlySpan<byte> ReadLengthDelimited()
        {
            var length = (int)ReadVarint();
            return ReadFixed(length);
        }

        public ReadOnlySpan<byte> ReadFixed(int length)
        {
            if (length < 0 || _position + length > _buffer.Length)
            {
                throw new InvalidDataException("Protobuf length-delimited field runs past the end of the buffer.");
            }

            var slice = _buffer.Slice(_position, length);
            _position += length;
            return slice;
        }

        public void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0:
                    ReadVarint();
                    break;
                case 1:
                    ReadFixed(8);
                    break;
                case 2:
                    ReadLengthDelimited();
                    break;
                case 5:
                    ReadFixed(4);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
            }
        }
    }
}

/// <summary>A decoded vector tile.</summary>
public sealed record MvtTile(IReadOnlyList<MvtLayer> Layers)
{
    /// <summary>Gets the named layer, or throws when the tile does not contain it.</summary>
    public MvtLayer Layer(string name)
        => Layers.FirstOrDefault(layer => string.Equals(layer.Name, name, StringComparison.Ordinal))
           ?? throw new InvalidDataException(
               $"Vector tile has no layer '{name}'. Layers present: {string.Join(", ", Layers.Select(layer => layer.Name))}.");

    /// <summary>Total feature count across every layer.</summary>
    public int FeatureCount => Layers.Sum(layer => layer.Features.Count);
}

/// <summary>A decoded vector tile layer.</summary>
public sealed record MvtLayer(string Name, uint Version, uint Extent, IReadOnlyList<MvtFeature> Features);

/// <summary>A decoded vector tile feature.</summary>
public sealed record MvtFeature(
    ulong? Id,
    MvtGeometryType GeometryType,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<IReadOnlyList<MvtPoint>> Rings)
{
    /// <summary>Every coordinate of the feature, in wire order.</summary>
    public IEnumerable<MvtPoint> Points => Rings.SelectMany(static ring => ring);
}

/// <summary>A coordinate in tile space: 0..extent across the tile, before any buffer is applied.</summary>
public readonly record struct MvtPoint(long X, long Y);

/// <summary>Vector tile geometry types (vector_tile.proto <c>GeomType</c>).</summary>
public enum MvtGeometryType
{
    /// <summary>Unknown or unset.</summary>
    Unknown = 0,

    /// <summary>Point or multipoint.</summary>
    Point = 1,

    /// <summary>Linestring or multilinestring.</summary>
    LineString = 2,

    /// <summary>Polygon or multipolygon.</summary>
    Polygon = 3
}
