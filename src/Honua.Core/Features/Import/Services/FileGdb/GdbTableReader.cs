// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Honua.Core.Features.Import.Services.FileGdb;

/// <summary>
/// Reads a FileGDB .gdbtable file, parsing the header, field definitions, and row data.
/// </summary>
internal sealed class GdbTableReader : IDisposable
{
    private const uint DeadBeefSentinel = 0xEFBEADDE; // little-endian 0xDEADBEEF

    private readonly BinaryReader _reader;
    private readonly string _tablePath;
    private readonly List<GdbField> _fields;
    private readonly int _nullBitmapSize;
    private readonly List<int> _nullableFieldIndices;
    private bool _disposed;

    /// <summary>
    /// Gets the file format version (3 or 4) from the file header.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// Gets the number of valid (non-deleted) rows.
    /// </summary>
    public int RowCount { get; }

    /// <summary>
    /// Gets the field definitions parsed from the table header.
    /// </summary>
    public IReadOnlyList<GdbField> Fields => _fields;

    /// <summary>
    /// Gets whether string values are stored as UTF-8 (vs UTF-16).
    /// Determined by bit 8 of the layer flags in the field description section.
    /// </summary>
    public bool UseUtf8Strings { get; private set; }

    /// <summary>
    /// Gets the field description version from the field description section.
    /// </summary>
    public int FieldDescVersion { get; private set; }

    private GdbTableReader(string tablePath)
    {
        _tablePath = tablePath;
        _fields = new List<GdbField>();
        _nullableFieldIndices = new List<int>();

        var stream = new FileStream(tablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        // Parse file header (40 bytes).
        Version = _reader.ReadInt32(); // bytes 0-3: signature/version
        if (Version is not (3 or 4))
        {
            throw new InvalidDataException($"Unsupported FileGDB table version: {Version}. Expected 3 or 4.");
        }

        RowCount = _reader.ReadInt32(); // bytes 4-7: numValidRows
        if (RowCount < 0)
        {
            throw new InvalidDataException(
                $"FileGDB table has an invalid numValidRows value ({RowCount}). The file is malformed.");
        }

        // Skip bytes 8-31 (header metadata we don't need).
        _reader.ReadBytes(24);

        var fieldDescOffset = _reader.ReadInt64(); // bytes 32-39

        // Seek to field description section and parse fields.
        stream.Seek(fieldDescOffset, SeekOrigin.Begin);
        ParseFieldDescriptions();

        // Compute null bitmap size: count nullable fields.
        for (var i = 0; i < _fields.Count; i++)
        {
            if (_fields[i].IsNullable)
            {
                _nullableFieldIndices.Add(i);
            }
        }

        _nullBitmapSize = (_nullableFieldIndices.Count + 7) / 8;
    }

    /// <summary>
    /// Opens and parses a FileGDB table from the given .gdbtable file path.
    /// </summary>
    public static GdbTableReader Open(string tablePath)
    {
        return new GdbTableReader(tablePath);
    }

    /// <summary>
    /// Gets the geometry field definition, if any geometry field exists.
    /// </summary>
    public GdbGeometryDef? GetGeometryDef()
    {
        for (var i = 0; i < _fields.Count; i++)
        {
            if (_fields[i].FieldType == GdbFieldType.Geometry)
            {
                return _fields[i].GeometryDef;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads all rows from the table, returning field values as dictionaries.
    /// Uses the .gdbtablx index file to locate row offsets.
    /// </summary>
    public IEnumerable<Dictionary<string, object?>> ReadRows()
    {
        var indexPath = Path.ChangeExtension(_tablePath, ".gdbtablx");
        if (!File.Exists(indexPath))
        {
            yield break;
        }

        using var indexStream = new FileStream(indexPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.SequentialScan
        });
        if (indexStream.Length < 16)
        {
            yield break;
        }

        using var indexReader = new BinaryReader(indexStream, Encoding.UTF8, leaveOpen: true);

        // Index header: 16 bytes.
        // Bytes 12-15: entry size (typically 5 for v3).
        indexStream.Seek(12, SeekOrigin.Begin);
        var entrySize = indexReader.ReadInt32();
        if (entrySize is < 4 or > 6)
        {
            entrySize = 5; // Default to 5 bytes per entry.
        }

        var numEntries = (indexStream.Length - 16) / entrySize;
        indexStream.Seek(16, SeekOrigin.Begin);
        var tableStream = _reader.BaseStream;
        var entryBuffer = new byte[6];
        var objectId = 0;

        for (var i = 0; i < numEntries; i++)
        {
            objectId++;
            if (indexStream.Read(entryBuffer, 0, entrySize) < entrySize)
            {
                yield break;
            }

            // Read row offset as a multi-byte little-endian value.
            long rowOffset = 0;
            for (var b = 0; b < entrySize; b++)
            {
                rowOffset |= (long)entryBuffer[b] << (b * 8);
            }

            if (rowOffset == 0)
            {
                continue; // Deleted row.
            }

            var row = ReadRow(tableStream, rowOffset, objectId);
            if (row != null)
            {
                yield return row;
            }
        }
    }

    private Dictionary<string, object?>? ReadRow(Stream tableStream, long offset, int objectId)
    {
        if (offset < 0 || offset > tableStream.Length - sizeof(int))
        {
            return null;
        }

        tableStream.Seek(offset, SeekOrigin.Begin);

        Span<byte> rowSizeBuffer = stackalloc byte[sizeof(int)];
        if (!TryReadExact(tableStream, rowSizeBuffer))
        {
            return null;
        }

        var rowSize = BinaryPrimitives.ReadInt32LittleEndian(rowSizeBuffer);
        // Hard cap at 256 MiB per row — FileGDB rows are tens of KB in practice;
        // anything larger is a malformed file and would make the subsequent
        // tableStream.Length subtraction underflow for very large claimed sizes.
        const int MaxRowSizeBytes = 256 * 1024 * 1024;
        if (rowSize <= 0 || rowSize > MaxRowSizeBytes)
        {
            return null;
        }

        // Safe form: add on the left, compare without subtraction risk.
        if ((long)offset + sizeof(int) + rowSize > tableStream.Length)
        {
            return null;
        }

        var rentedBuffer = ArrayPool<byte>.Shared.Rent(rowSize);
        try
        {
            var rowData = rentedBuffer.AsSpan(0, rowSize);
            if (!TryReadExact(tableStream, rowData))
            {
                return null;
            }

            return ReadRow(rowData, objectId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private Dictionary<string, object?>? ReadRow(ReadOnlySpan<byte> rowData, int objectId)
    {
        var result = new Dictionary<string, object?>(_fields.Count);

        // Read null bitmap.
        if (_nullBitmapSize > rowData.Length)
        {
            return null;
        }

        var nullBitmap = rowData[.._nullBitmapSize];
        var pos = _nullBitmapSize;

        var nullableIndex = 0;

        for (var fieldIndex = 0; fieldIndex < _fields.Count; fieldIndex++)
        {
            var field = _fields[fieldIndex];

            // ObjectID is not stored in row data; it's implied from the index.
            if (field.FieldType == GdbFieldType.ObjectId)
            {
                result[field.Name] = objectId;
                continue;
            }

            // Check null bitmap for nullable fields.
            var isNull = false;
            if (field.IsNullable)
            {
                var byteIndex = nullableIndex / 8;
                var bitIndex = nullableIndex % 8;
                isNull = byteIndex < nullBitmap.Length && (nullBitmap[byteIndex] & (1 << bitIndex)) != 0;
                nullableIndex++;
            }

            if (isNull)
            {
                result[field.Name] = null;
                continue;
            }

            var value = ReadFieldValue(field, rowData, ref pos);
            result[field.Name] = value;
        }

        return result;
    }

    private static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    private object? ReadFieldValue(GdbField field, ReadOnlySpan<byte> rowData, ref int pos)
    {
        if (pos >= rowData.Length)
        {
            return null;
        }

        switch (field.FieldType)
        {
            case GdbFieldType.Int16:
                if (pos + 2 > rowData.Length) return null;
                var i16 = BitConverter.ToInt16(rowData[pos..]);
                pos += 2;
                return (int)i16;

            case GdbFieldType.Int32:
                if (pos + 4 > rowData.Length) return null;
                var i32 = BitConverter.ToInt32(rowData[pos..]);
                pos += 4;
                return i32;

            case GdbFieldType.Float32:
                if (pos + 4 > rowData.Length) return null;
                var f32 = BitConverter.ToSingle(rowData[pos..]);
                pos += 4;
                return (double)f32;

            case GdbFieldType.Float64:
                if (pos + 8 > rowData.Length) return null;
                var f64 = BitConverter.ToDouble(rowData[pos..]);
                pos += 8;
                return f64;

            case GdbFieldType.String:
                return ReadStringValue(rowData, ref pos);

            case GdbFieldType.DateTime:
                if (pos + 8 > rowData.Length) return null;
                var days = BitConverter.ToDouble(rowData[pos..]);
                pos += 8;
                return ConvertFileGdbDateTime(days);

            case GdbFieldType.Geometry:
                return ReadBlobValue(rowData, ref pos);

            case GdbFieldType.Binary:
                return ReadBlobValue(rowData, ref pos);

            case GdbFieldType.Uuid:
            case GdbFieldType.GlobalId:
                if (pos + 16 > rowData.Length) return null;
                var guidBytes = rowData.Slice(pos, 16).ToArray();
                pos += 16;
                return FormatGuid(guidBytes);

            case GdbFieldType.Xml:
                return ReadStringBlobValue(rowData, ref pos);

            case GdbFieldType.Int64:
                if (pos + 8 > rowData.Length) return null;
                var i64 = BitConverter.ToInt64(rowData[pos..]);
                pos += 8;
                return i64;

            case GdbFieldType.DateOnly:
            case GdbFieldType.TimeOnly:
            case GdbFieldType.DateTimeOffset:
                if (pos + 8 > rowData.Length) return null;
                var dtVal = BitConverter.ToDouble(rowData[pos..]);
                pos += 8;
                return ConvertFileGdbDateTime(dtVal);

            default:
                return null;
        }
    }

    private string? ReadStringValue(ReadOnlySpan<byte> data, ref int pos)
    {
        var byteCount = (int)GdbVarInt.ReadVarUInt(data, ref pos);
        if (byteCount <= 0 || pos + byteCount > data.Length)
        {
            pos += Math.Max(0, Math.Min(byteCount, data.Length - pos));
            return null;
        }

        string value;
        if (UseUtf8Strings)
        {
            value = Encoding.UTF8.GetString(data.Slice(pos, byteCount));
        }
        else
        {
            value = Encoding.Unicode.GetString(data.Slice(pos, byteCount));
        }

        pos += byteCount;
        return value;
    }

    private static byte[]? ReadBlobValue(ReadOnlySpan<byte> data, ref int pos)
    {
        var byteCount = (int)GdbVarInt.ReadVarUInt(data, ref pos);
        if (byteCount <= 0 || pos + byteCount > data.Length)
        {
            pos += Math.Max(0, Math.Min(byteCount, data.Length - pos));
            return null;
        }

        var blob = data.Slice(pos, byteCount).ToArray();
        pos += byteCount;
        return blob;
    }

    private static string? ReadStringBlobValue(ReadOnlySpan<byte> data, ref int pos)
    {
        var byteCount = (int)GdbVarInt.ReadVarUInt(data, ref pos);
        if (byteCount <= 0 || pos + byteCount > data.Length)
        {
            pos += Math.Max(0, Math.Min(byteCount, data.Length - pos));
            return null;
        }

        var value = Encoding.UTF8.GetString(data.Slice(pos, byteCount));
        pos += byteCount;
        return value;
    }

    private static string FormatGuid(byte[] bytes)
    {
        // FileGDB stores GUIDs in mixed-endian format (same as COM/OLE).
        return new Guid(bytes).ToString("B").ToUpperInvariant();
    }

    private static DateTime? ConvertFileGdbDateTime(double fileGdbDays)
    {
        if (double.IsNaN(fileGdbDays) || double.IsInfinity(fileGdbDays))
        {
            return null;
        }

        // FileGDB date epoch: December 30, 1899 00:00:00 (OLE Automation date).
        try
        {
            return DateTime.FromOADate(fileGdbDays);
        }
        catch
        {
            return null;
        }
    }

    private void ParseFieldDescriptions()
    {
        var fieldDescSize = _reader.ReadInt32(); // Size of field description (excluding this int32).
        FieldDescVersion = _reader.ReadInt32(); // Version (3 or 4).

        if (FieldDescVersion < 3)
        {
            throw new InvalidDataException($"Unsupported field description version: {FieldDescVersion}.");
        }

        var layerFlags = _reader.ReadUInt32();
        // Bits 0-7: layer geometry type (used by the layer, not individual fields).
        // Bit 8: if set, string field values in row data are stored as UTF-8 instead of UTF-16.
        // Bit 30: layer has M values.
        // Bit 31: layer has Z values.
        UseUtf8Strings = (layerFlags & 0x100) != 0;
        var layerHasZ = (layerFlags & 0x80000000) != 0;
        var layerHasM = (layerFlags & 0x40000000) != 0;

        var fieldCount = _reader.ReadUInt16();

        for (var i = 0; i < fieldCount; i++)
        {
            var field = ReadFieldDescriptor(layerHasZ, layerHasM);
            _fields.Add(field);
        }
    }

    private GdbField ReadFieldDescriptor(bool layerHasZ, bool layerHasM)
    {
        // Field name (UTF-16LE).
        var nameCharCount = _reader.ReadByte();
        var nameBytes = _reader.ReadBytes(nameCharCount * 2);
        var name = Encoding.Unicode.GetString(nameBytes);

        // Field alias (UTF-16LE).
        var aliasCharCount = _reader.ReadByte();
        var alias = string.Empty;
        if (aliasCharCount > 0)
        {
            var aliasBytes = _reader.ReadBytes(aliasCharCount * 2);
            alias = Encoding.Unicode.GetString(aliasBytes);
        }

        var fieldType = (GdbFieldType)_reader.ReadByte();

        return fieldType switch
        {
            GdbFieldType.ObjectId => ReadObjectIdField(name, alias, fieldType),
            GdbFieldType.Geometry => ReadGeometryField(name, alias, layerHasZ, layerHasM),
            GdbFieldType.String => ReadStringField(name, alias),
            _ => ReadGenericField(name, alias, fieldType),
        };
    }

    private GdbField ReadObjectIdField(string name, string alias, GdbFieldType fieldType)
    {
        var width = _reader.ReadByte();
        var flags = _reader.ReadByte();
        _ = width;

        return new GdbField
        {
            Name = name,
            Alias = alias,
            FieldType = fieldType,
            IsNullable = false, // ObjectID is never nullable.
        };
    }

    private GdbField ReadStringField(string name, string alias)
    {
        var maxLength = _reader.ReadInt32();
        var flags = _reader.ReadByte();
        var isNullable = (flags & 1) != 0;

        // Has default value (bit 2).
        if ((flags & 4) != 0)
        {
            var defaultLen = (int)GdbVarInt.ReadVarUInt(_reader);
            if (defaultLen > 0)
            {
                _reader.ReadBytes(defaultLen);
            }
        }

        return new GdbField
        {
            Name = name,
            Alias = alias,
            FieldType = GdbFieldType.String,
            IsNullable = isNullable,
            MaxLength = maxLength,
        };
    }

    private GdbField ReadGeometryField(string name, string alias, bool layerHasZ, bool layerHasM)
    {
        var unknownByte = _reader.ReadByte();
        var flags = _reader.ReadByte();
        var isNullable = (flags & 1) != 0;
        _ = unknownByte;

        // WKT spatial reference (stored as UTF-16LE).
        var wktByteLen = _reader.ReadUInt16();
        string? wkt = null;
        if (wktByteLen > 0)
        {
            var wktBytes = _reader.ReadBytes(wktByteLen);
            wkt = Encoding.Unicode.GetString(wktBytes);
        }

        // Geometry dimension flags.
        var geomFlags = _reader.ReadByte();
        var hasZ = (geomFlags & 1) != 0;
        var hasM = (geomFlags & 2) != 0;

        // XY origin and scale.
        var xOrigin = _reader.ReadDouble();
        var yOrigin = _reader.ReadDouble();
        var xyScale = _reader.ReadDouble();

        // Z origin and scale (if hasZ).
        double zOrigin = 0, zScale = 1;
        if (hasZ)
        {
            zOrigin = _reader.ReadDouble();
            zScale = _reader.ReadDouble();
        }

        // M origin and scale (if hasM).
        double mOrigin = 0, mScale = 1;
        if (hasM)
        {
            mOrigin = _reader.ReadDouble();
            mScale = _reader.ReadDouble();
        }

        // Tolerances (field desc version >= 3).
        double xyTolerance = 0;
        if (FieldDescVersion >= 3)
        {
            xyTolerance = _reader.ReadDouble();
            if (hasZ) _reader.ReadDouble(); // Z tolerance.
            if (hasM) _reader.ReadDouble(); // M tolerance.
        }

        // Extent: xmin, ymin, xmax, ymax.
        _reader.ReadDouble(); // xmin
        _reader.ReadDouble(); // ymin
        _reader.ReadDouble(); // xmax
        _reader.ReadDouble(); // ymax

        // Z extent (present when the layer stores Z values).
        if (layerHasZ)
        {
            _reader.ReadDouble(); // zmin
            _reader.ReadDouble(); // zmax
        }

        // M extent (present when the layer stores M values).
        if (layerHasM)
        {
            _reader.ReadDouble(); // mmin
            _reader.ReadDouble(); // mmax
        }

        // Spatial grid info.
        var numGrids = _reader.ReadByte();
        for (var g = 0; g < numGrids; g++)
        {
            _reader.ReadDouble(); // grid size
        }

        // Version >= 4: extra spatial index data.
        if (FieldDescVersion >= 4)
        {
            var extraCount = _reader.ReadInt32();
            for (var e = 0; e < extraCount; e++)
            {
                _reader.ReadDouble();
            }
        }

        var srid = DetectSridFromWkt(wkt);

        return new GdbField
        {
            Name = name,
            Alias = alias,
            FieldType = GdbFieldType.Geometry,
            IsNullable = isNullable,
            GeometryDef = new GdbGeometryDef
            {
                WktSpatialReference = wkt,
                Srid = srid,
                XOrigin = xOrigin,
                YOrigin = yOrigin,
                XYScale = xyScale,
                ZOrigin = zOrigin,
                ZScale = zScale,
                MOrigin = mOrigin,
                MScale = mScale,
                XYTolerance = xyTolerance,
                HasZ = hasZ,
                HasM = hasM,
            },
        };
    }

    private GdbField ReadGenericField(string name, string alias, GdbFieldType fieldType)
    {
        var width = _reader.ReadByte();
        var flags = _reader.ReadByte();
        var isNullable = (flags & 1) != 0;
        _ = width;

        // Types that support default values: Int16, Int32, Float32, Float64, DateTime.
        // Types that do NOT read defaults: Binary, Uuid, GlobalId, Xml, Raster.
        var supportsDefault = fieldType is
            GdbFieldType.Int16 or
            GdbFieldType.Int32 or
            GdbFieldType.Float32 or
            GdbFieldType.Float64 or
            GdbFieldType.DateTime or
            GdbFieldType.Int64 or
            GdbFieldType.DateOnly or
            GdbFieldType.TimeOnly or
            GdbFieldType.DateTimeOffset;

        if (supportsDefault && (flags & 4) != 0)
        {
            var defaultLen = (int)GdbVarInt.ReadVarUInt(_reader);
            if (defaultLen > 0)
            {
                _reader.ReadBytes(defaultLen);
            }
        }

        return new GdbField
        {
            Name = name,
            Alias = alias,
            FieldType = fieldType,
            IsNullable = isNullable,
        };
    }

    private static int DetectSridFromWkt(string? wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return 0;
        }

        // Use LastIndexOf so we find the outermost CRS AUTHORITY, not a nested
        // datum/spheroid AUTHORITY that appears earlier in projected WKT.
        var authorityIndex = wkt.LastIndexOf("AUTHORITY[", StringComparison.OrdinalIgnoreCase);
        if (authorityIndex >= 0)
        {
            var epsgStart = wkt.IndexOf("\"EPSG\"", authorityIndex, StringComparison.OrdinalIgnoreCase);
            if (epsgStart >= 0)
            {
                var codeStart = wkt.IndexOf(',', epsgStart);
                if (codeStart >= 0)
                {
                    codeStart++; // Skip comma.
                    var codeEnd = wkt.IndexOf(']', codeStart);
                    if (codeEnd > codeStart)
                    {
                        var codeStr = wkt[codeStart..codeEnd].Trim().Trim('"');
                        if (int.TryParse(codeStr, out var srid))
                        {
                            return srid;
                        }
                    }
                }
            }
        }

        // Fallback: detect from well-known datum names.
        if (wkt.Contains("GCS_WGS_1984", StringComparison.OrdinalIgnoreCase) ||
            wkt.Contains("D_WGS_1984", StringComparison.OrdinalIgnoreCase))
        {
            // Only if it's NOT a projected CRS.
            if (!wkt.Contains("PROJCS", StringComparison.OrdinalIgnoreCase))
            {
                return 4326;
            }
        }

        if (wkt.Contains("GCS_North_American_1983", StringComparison.OrdinalIgnoreCase) ||
            wkt.Contains("D_North_American_1983", StringComparison.OrdinalIgnoreCase))
        {
            if (!wkt.Contains("PROJCS", StringComparison.OrdinalIgnoreCase))
            {
                return 4269;
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _reader.Dispose();
            _disposed = true;
        }
    }
}
