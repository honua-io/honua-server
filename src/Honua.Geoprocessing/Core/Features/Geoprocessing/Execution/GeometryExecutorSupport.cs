// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.ControlPlane;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

internal readonly record struct SingleGeometryInputs(byte[] WkbBytes, int Srid);

internal readonly record struct MeasuredGeometryInputs(
    byte[] WkbBytes,
    int Srid,
    bool Geodesic,
    string Unit,
    double MetersPerUnit);

internal readonly record struct PairGeometryInputs(string FirstWkb, string SecondWkb, int Srid);

internal static class GeometryExecutorSupport
{
    private const string ScalarDataUriPrefix = "data:application/json;base64,";

    public static bool TryReadSingleGeometryInputs(
        IReadOnlyDictionary<string, string> parameters,
        out SingleGeometryInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            error = "missing required input 'wkb'";
            return false;
        }

        if (!TryReadPositiveSrid(parameters, prefix, out var srid, out error))
        {
            return false;
        }

        if (!TryDecodeBase64Wkb(wkb, out var wkbBytes, out error))
        {
            return false;
        }

        inputs = new SingleGeometryInputs(wkbBytes, srid);
        error = "";
        return true;
    }

    public static bool TryReadMeasuredGeometryInputs(
        IReadOnlyDictionary<string, string> parameters,
        bool isArea,
        out MeasuredGeometryInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            error = "missing required input 'wkb'";
            return false;
        }

        if (!TryReadPositiveSrid(parameters, prefix, out var srid, out error))
        {
            return false;
        }

        var geodesic = false;
        if (parameters.TryGetValue(prefix + "geodesic", out var geodesicRaw)
            && !string.IsNullOrWhiteSpace(geodesicRaw)
            && !bool.TryParse(geodesicRaw, out geodesic))
        {
            error = "invalid input 'geodesic'; expected boolean";
            return false;
        }

        var unitRaw = parameters.TryGetValue(prefix + "unit", out var rawUnit) ? rawUnit : null;
        string unitName;
        double unitFactor;
        if (isArea)
        {
            if (!GeodesicGeometryOperations.TryNormalizeAreaUnit(unitRaw, out unitName, out unitFactor))
            {
                error = $"invalid input 'unit' '{unitRaw}'; expected square-meters, square-kilometers, hectares, acres, square-feet, or square-miles";
                return false;
            }
        }
        else
        {
            if (!GeodesicGeometryOperations.TryNormalizeLinearUnit(unitRaw, out unitName, out unitFactor))
            {
                error = $"invalid input 'unit' '{unitRaw}'; expected meters, kilometers, feet, yards, miles, or nautical-miles";
                return false;
            }
        }

        if (!TryDecodeBase64Wkb(wkb, out var wkbBytes, out error))
        {
            return false;
        }

        inputs = new MeasuredGeometryInputs(wkbBytes, srid, geodesic, unitName, unitFactor);
        error = "";
        return true;
    }

    public static bool TryReadPairGeometryInputs(
        IReadOnlyDictionary<string, string> parameters,
        string firstInputName,
        string secondInputName,
        out PairGeometryInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + firstInputName, out var first) || string.IsNullOrWhiteSpace(first))
        {
            error = $"missing required input '{firstInputName}'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + secondInputName, out var second) || string.IsNullOrWhiteSpace(second))
        {
            error = $"missing required input '{secondInputName}'";
            return false;
        }

        if (!TryReadPositiveSrid(parameters, prefix, out var srid, out error))
        {
            return false;
        }

        inputs = new PairGeometryInputs(first, second, srid);
        error = "";
        return true;
    }

    public static bool TryDecodeGeometry(string base64Wkb, out Geometry geometry, out string error)
    {
        geometry = null!;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Wkb);
        }
        catch (FormatException)
        {
            error = "is not valid base64";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "decoded to zero bytes";
            return false;
        }

        try
        {
            var reader = new WKBReader { HandleSRID = true };
            geometry = reader.Read(bytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            error = $"could not be decoded ({ex.GetType().Name})";
            return false;
        }

        if (geometry == null || geometry.IsEmpty)
        {
            error = "decoded to an empty geometry";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryDecodeWkbAndValidateSrid(
        byte[] wkbBytes,
        int srid,
        out Geometry geometry,
        out string? wkbError,
        out bool isEmpty,
        out string? sridError)
    {
        geometry = null!;
        wkbError = null;
        isEmpty = false;
        sridError = null;

        try
        {
            var reader = new WKBReader { HandleSRID = true };
            geometry = reader.Read(wkbBytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            wkbError = ex.Message;
            return false;
        }

        if (geometry == null || geometry.IsEmpty)
        {
            isEmpty = true;
            return false;
        }

        if (!GeometrySridGuard.TryValidateEmbeddedSrid(geometry, srid, out var embeddedError))
        {
            sridError = embeddedError;
            return false;
        }

        geometry.SRID = srid;
        return true;
    }

    public static byte[] BuildMeasurePayload(
        string processId,
        string measure,
        double value,
        string unit,
        int inputSrid,
        string inputGeometryType)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MeasureResult");
            writer.WriteString("processId", processId);
            writer.WriteString("measure", measure);
            writer.WriteNumber("value", value);
            writer.WriteString("unit", unit);
            writer.WriteNumber("inputSrid", inputSrid);
            writer.WriteString("inputGeometryType", inputGeometryType);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static string BuildJsonDataUri(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var sb = new StringBuilder(payload.Length * 2 + ScalarDataUriPrefix.Length);
        sb.Append(ScalarDataUriPrefix);
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private static bool TryReadPositiveSrid(
        IReadOnlyDictionary<string, string> parameters,
        string prefix,
        out int srid,
        out string error)
    {
        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid)
            || srid <= 0)
        {
            srid = 0;
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryDecodeBase64Wkb(string wkb, out byte[] wkbBytes, out string error)
    {
        try
        {
            wkbBytes = Convert.FromBase64String(wkb);
        }
        catch (FormatException)
        {
            wkbBytes = [];
            error = "input 'wkb' is not valid base64";
            return false;
        }

        if (wkbBytes.Length == 0)
        {
            error = "input 'wkb' decoded to zero bytes";
            return false;
        }

        error = "";
        return true;
    }
}
