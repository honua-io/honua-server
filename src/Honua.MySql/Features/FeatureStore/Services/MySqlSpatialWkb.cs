// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO;
using NetTopologySuite.IO;

namespace Honua.MySql.Features.FeatureStore.Services;

/// <summary>
/// Normalizes filter geometry bytes for MySQL/MariaDB's plain-WKB constructors.
/// </summary>
internal static class MySqlSpatialWkb
{
    [ThreadStatic]
    private static WKBReader? _reader;

    [ThreadStatic]
    private static WKBWriter? _writer;

    /// <summary>
    /// Removes EWKB metadata such as an embedded SRID. The database constructor receives the
    /// authoritative SRID separately, so passing SRID-bearing EWKB would shift MySQL's parser.
    /// Malformed input is left unchanged so the database retains responsibility for diagnostics.
    /// </summary>
    public static byte[] ToPlainWkb(byte[] wkb)
    {
        ArgumentNullException.ThrowIfNull(wkb);

        try
        {
            _reader ??= new WKBReader();
            _writer ??= new WKBWriter(ByteOrder.LittleEndian, handleSRID: false);
            return _writer.Write(_reader.Read(wkb));
        }
        catch (Exception ex) when (ex is ParseException or EndOfStreamException or ArgumentException or IndexOutOfRangeException)
        {
            return wkb;
        }
    }
}
