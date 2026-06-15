// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.Ogc.Api.Coverages.Services;

/// <summary>
/// One domain axis of a CoverageJSON grid response. Values are regularly
/// spaced and described by start/stop/num; spatial axes carry CRS coordinates
/// while non-referenced axes carry grid indices.
/// </summary>
/// <param name="Name">Axis name (matches the source dimension name).</param>
/// <param name="Start">First cell value.</param>
/// <param name="Stop">Last cell value.</param>
/// <param name="Count">Cell count.</param>
internal sealed record CoverageJsonAxis(string Name, double Start, double Stop, int Count);

/// <summary>
/// AOT-safe CoverageJSON (CovJSON 1.0) writer for Zarr-backed coverage
/// subsets. Emits a Grid-domain coverage with one NdArray range built with
/// <see cref="Utf8JsonWriter"/> because the document shape (axis names, value
/// arrays) is data-driven and unsuitable for source-generated serializer types.
/// </summary>
internal static class CoverageJsonWriter
{
    /// <summary>
    /// Writes a CoverageJSON document for one decoded Zarr subset.
    /// </summary>
    /// <param name="result">Decoded subset payload (little-endian buffer).</param>
    /// <param name="axes">Domain axes in the same order as the subset shape.</param>
    /// <param name="xAxisName">Axis name carrying CRS x coordinates, when georeferenced.</param>
    /// <param name="yAxisName">Axis name carrying CRS y coordinates, when georeferenced.</param>
    /// <param name="srid">EPSG SRID for the spatial axes; 0 when not georeferenced.</param>
    /// <returns>UTF-8 encoded CoverageJSON payload.</returns>
    public static byte[] Write(
        ZarrSubsetResult result,
        IReadOnlyList<CoverageJsonAxis> axes,
        string? xAxisName,
        string? yAxisName,
        int srid)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(axes);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Coverage");

            WriteDomain(writer, axes, xAxisName, yAxisName, srid);
            WriteParameters(writer, result.Variable);
            WriteRanges(writer, result, axes);

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteDomain(
        Utf8JsonWriter writer,
        IReadOnlyList<CoverageJsonAxis> axes,
        string? xAxisName,
        string? yAxisName,
        int srid)
    {
        writer.WriteStartObject("domain");
        writer.WriteString("type", "Domain");
        writer.WriteString("domainType", "Grid");

        writer.WriteStartObject("axes");
        foreach (var axis in axes)
        {
            writer.WriteStartObject(axis.Name);
            writer.WriteNumber("start", axis.Start);
            writer.WriteNumber("stop", axis.Stop);
            writer.WriteNumber("num", axis.Count);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        if (srid > 0 && xAxisName is not null && yAxisName is not null)
        {
            writer.WriteStartArray("referencing");
            writer.WriteStartObject();
            writer.WriteStartArray("coordinates");
            writer.WriteStringValue(xAxisName);
            writer.WriteStringValue(yAxisName);
            writer.WriteEndArray();
            writer.WriteStartObject("system");
            writer.WriteString("type", srid == 4326 ? "GeographicCRS" : "ProjectedCRS");
            writer.WriteString("id", FormattableString.Invariant($"http://www.opengis.net/def/crs/EPSG/0/{srid}"));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteParameters(Utf8JsonWriter writer, string variable)
    {
        writer.WriteStartObject("parameters");
        writer.WriteStartObject(variable);
        writer.WriteString("type", "Parameter");
        writer.WriteStartObject("observedProperty");
        writer.WriteStartObject("label");
        writer.WriteString("en", variable);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteRanges(Utf8JsonWriter writer, ZarrSubsetResult result, IReadOnlyList<CoverageJsonAxis> axes)
    {
        writer.WriteStartObject("ranges");
        writer.WriteStartObject(result.Variable);
        writer.WriteString("type", "NdArray");
        writer.WriteString("dataType", ResolveCovJsonDataType(result.DataType));

        writer.WriteStartArray("axisNames");
        foreach (var axis in axes)
        {
            writer.WriteStringValue(axis.Name);
        }
        writer.WriteEndArray();

        writer.WriteStartArray("shape");
        foreach (var size in result.Shape)
        {
            writer.WriteNumberValue(size);
        }
        writer.WriteEndArray();

        writer.WriteStartArray("values");
        WriteValues(writer, result);
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string ResolveCovJsonDataType(string dtype)
        => NormalizeDataType(dtype) is ['f', ..] ? "float" : "integer";

    private static void WriteValues(Utf8JsonWriter writer, ZarrSubsetResult result)
    {
        var data = result.Data.AsSpan();
        switch (NormalizeDataType(result.DataType))
        {
            case "f4":
                for (var offset = 0; offset + 4 <= data.Length; offset += 4)
                {
                    WriteFloatingPoint(writer, BinaryPrimitives.ReadSingleLittleEndian(data[offset..]));
                }
                break;
            case "f8":
                for (var offset = 0; offset + 8 <= data.Length; offset += 8)
                {
                    WriteFloatingPoint(writer, BinaryPrimitives.ReadDoubleLittleEndian(data[offset..]));
                }
                break;
            case "i1":
                foreach (var value in data)
                {
                    writer.WriteNumberValue((sbyte)value);
                }
                break;
            case "u1":
            case "b1":
                foreach (var value in data)
                {
                    writer.WriteNumberValue(value);
                }
                break;
            case "i2":
                for (var offset = 0; offset + 2 <= data.Length; offset += 2)
                {
                    writer.WriteNumberValue(BinaryPrimitives.ReadInt16LittleEndian(data[offset..]));
                }
                break;
            case "u2":
                for (var offset = 0; offset + 2 <= data.Length; offset += 2)
                {
                    writer.WriteNumberValue(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]));
                }
                break;
            case "i4":
                for (var offset = 0; offset + 4 <= data.Length; offset += 4)
                {
                    writer.WriteNumberValue(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
                }
                break;
            case "u4":
                for (var offset = 0; offset + 4 <= data.Length; offset += 4)
                {
                    writer.WriteNumberValue(BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));
                }
                break;
            case "i8":
                for (var offset = 0; offset + 8 <= data.Length; offset += 8)
                {
                    writer.WriteNumberValue(BinaryPrimitives.ReadInt64LittleEndian(data[offset..]));
                }
                break;
            case "u8":
                for (var offset = 0; offset + 8 <= data.Length; offset += 8)
                {
                    writer.WriteNumberValue(BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"Zarr dtype '{result.DataType}' is not supported for CoverageJSON output.");
        }
    }

    private static void WriteFloatingPoint(Utf8JsonWriter writer, double value)
    {
        if (double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            // NaN / Infinity are not representable in JSON; CovJSON uses null for missing data.
            writer.WriteNullValue();
        }
    }

    private static string NormalizeDataType(string dtype)
        => dtype.Length > 0 && dtype[0] is '<' or '|' or '=' ? dtype[1..] : dtype;
}
