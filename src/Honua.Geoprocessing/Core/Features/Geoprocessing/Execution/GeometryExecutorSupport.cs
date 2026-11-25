// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.ControlPlane;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

internal static partial class GeometryExecutorSupport
{
    private const string ScalarDataUriPrefix = "data:application/json;base64,";

    private enum MeasurementKind
    {
        Area,
        Length,
    }

    private readonly record struct PositiveSridReadResult(int Srid, string? Error);

    private readonly record struct Base64WkbDecodeResult(byte[] WkbBytes, string? Error);

    private readonly record struct MeasurementModeReadResult(MeasurementMode Mode, string? Error);

    private readonly record struct MeasurementUnitReadResult(string Unit, double MetersPerUnit, string? Error);

    public static SingleGeometryInputReadResult ReadSingleGeometryInputs(
        IReadOnlyDictionary<string, string> parameters)
    {
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        if (!parameters.TryGetValue(prefix + "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            return new SingleGeometryInputReadResult(default, "missing required input 'wkb'");
        }

        var sridRead = ReadPositiveSrid(parameters, prefix);
        if (sridRead.Error is not null)
        {
            return new SingleGeometryInputReadResult(default, sridRead.Error);
        }

        var wkbDecode = DecodeBase64Wkb(wkb);
        if (wkbDecode.Error is not null)
        {
            return new SingleGeometryInputReadResult(default, wkbDecode.Error);
        }

        return new SingleGeometryInputReadResult(new SingleGeometryInputs(wkbDecode.WkbBytes, sridRead.Srid), null);
    }

    public static MeasuredGeometryInputReadResult ReadAreaGeometryInputs(
        IReadOnlyDictionary<string, string> parameters)
        => ReadMeasuredGeometryInputs(parameters, MeasurementKind.Area);

    public static MeasuredGeometryInputReadResult ReadLengthGeometryInputs(
        IReadOnlyDictionary<string, string> parameters)
        => ReadMeasuredGeometryInputs(parameters, MeasurementKind.Length);

    public static PairGeometryInputReadResult ReadPairGeometryInputs(
        IReadOnlyDictionary<string, string> parameters,
        PairGeometryInputNames inputNames)
    {
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        if (!parameters.TryGetValue(prefix + inputNames.FirstInputName, out var first) || string.IsNullOrWhiteSpace(first))
        {
            return new PairGeometryInputReadResult(default, $"missing required input '{inputNames.FirstInputName}'");
        }

        if (!parameters.TryGetValue(prefix + inputNames.SecondInputName, out var second) || string.IsNullOrWhiteSpace(second))
        {
            return new PairGeometryInputReadResult(default, $"missing required input '{inputNames.SecondInputName}'");
        }

        var sridRead = ReadPositiveSrid(parameters, prefix);
        if (sridRead.Error is not null)
        {
            return new PairGeometryInputReadResult(default, sridRead.Error);
        }

        return new PairGeometryInputReadResult(new PairGeometryInputs(first, second, sridRead.Srid), null);
    }

    public static GeometryDecodeResult DecodeGeometry(string base64Wkb)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Wkb);
        }
        catch (FormatException)
        {
            return new GeometryDecodeResult(null, "is not valid base64");
        }

        if (bytes.Length == 0)
        {
            return new GeometryDecodeResult(null, "decoded to zero bytes");
        }

        Geometry geometry;
        try
        {
            var reader = new WKBReader { HandleSRID = true };
            geometry = reader.Read(bytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            return new GeometryDecodeResult(null, $"could not be decoded ({ex.GetType().Name})");
        }

        return geometry == null || geometry.IsEmpty
            ? new GeometryDecodeResult(null, "decoded to an empty geometry")
            : new GeometryDecodeResult(geometry, null);
    }

    public static ValidatedGeometryDecodeResult DecodeWkbAndValidateSrid(byte[] wkbBytes, int srid)
    {
        Geometry geometry;
        try
        {
            var reader = new WKBReader { HandleSRID = true };
            geometry = reader.Read(wkbBytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            return new ValidatedGeometryDecodeResult
            {
                FailureKind = WkbDecodeFailureKind.InvalidWkb,
                Error = ex.Message,
            };
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return new ValidatedGeometryDecodeResult { FailureKind = WkbDecodeFailureKind.EmptyGeometry };
        }

        if (!GeometrySridGuard.TryValidateEmbeddedSrid(geometry, srid, out var embeddedError))
        {
            return new ValidatedGeometryDecodeResult
            {
                FailureKind = WkbDecodeFailureKind.InvalidSrid,
                Error = embeddedError,
            };
        }

        geometry.SRID = srid;
        return new ValidatedGeometryDecodeResult
        {
            Geometry = geometry,
            FailureKind = WkbDecodeFailureKind.None,
        };
    }

    public static byte[] BuildMeasurePayload(MeasurePayloadRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MeasureResult");
            writer.WriteString("processId", request.ProcessId);
            writer.WriteString("measure", request.Measure);
            writer.WriteNumber("value", request.Value);
            writer.WriteString("unit", request.Unit);
            writer.WriteNumber("inputSrid", request.InputSrid);
            writer.WriteString("inputGeometryType", request.InputGeometryType);
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

    private readonly record struct MeasurementSettingsReadResult
    {
        public MeasurementMode Mode { get; init; }

        public string Unit { get; init; }

        public double MetersPerUnit { get; init; }

        public string? Error { get; init; }
    }

    private static MeasuredGeometryInputReadResult ReadMeasuredGeometryInputs(
        IReadOnlyDictionary<string, string> parameters,
        MeasurementKind kind)
    {
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        if (!parameters.TryGetValue(prefix + "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            return new MeasuredGeometryInputReadResult(default, "missing required input 'wkb'");
        }

        var sridRead = ReadPositiveSrid(parameters, prefix);
        if (sridRead.Error is not null)
        {
            return new MeasuredGeometryInputReadResult(default, sridRead.Error);
        }

        var settingsRead = ReadMeasurementSettings(parameters, prefix, kind);
        if (settingsRead.Error is not null)
        {
            return new MeasuredGeometryInputReadResult(default, settingsRead.Error);
        }

        var wkbDecode = DecodeBase64Wkb(wkb);
        if (wkbDecode.Error is not null)
        {
            return new MeasuredGeometryInputReadResult(default, wkbDecode.Error);
        }

        var inputs = new MeasuredGeometryInputs
        {
            WkbBytes = wkbDecode.WkbBytes,
            Srid = sridRead.Srid,
            Mode = settingsRead.Mode,
            Unit = settingsRead.Unit,
            MetersPerUnit = settingsRead.MetersPerUnit,
        };
        return new MeasuredGeometryInputReadResult(inputs, null);
    }

    private static MeasurementSettingsReadResult ReadMeasurementSettings(
        IReadOnlyDictionary<string, string> parameters,
        string prefix,
        MeasurementKind kind)
    {
        var modeRead = ReadMeasurementMode(parameters, prefix);
        if (modeRead.Error is not null)
        {
            return new MeasurementSettingsReadResult { Mode = MeasurementMode.Planar, Unit = string.Empty, Error = modeRead.Error };
        }

        var unitRead = ReadMeasurementUnit(parameters, prefix, kind);
        if (unitRead.Error is not null)
        {
            return new MeasurementSettingsReadResult { Mode = modeRead.Mode, Unit = string.Empty, Error = unitRead.Error };
        }

        return new MeasurementSettingsReadResult
        {
            Mode = modeRead.Mode,
            Unit = unitRead.Unit,
            MetersPerUnit = unitRead.MetersPerUnit,
        };
    }

    private static PositiveSridReadResult ReadPositiveSrid(
        IReadOnlyDictionary<string, string> parameters,
        string prefix)
    {
        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            return new PositiveSridReadResult(0, "missing or invalid input 'srid'; expected a positive integer");
        }

        return new PositiveSridReadResult(srid, null);
    }

    private static MeasurementModeReadResult ReadMeasurementMode(
        IReadOnlyDictionary<string, string> parameters,
        string prefix)
    {
        if (!parameters.TryGetValue(prefix + "geodesic", out var geodesicRaw)
            || string.IsNullOrWhiteSpace(geodesicRaw))
        {
            return new MeasurementModeReadResult(MeasurementMode.Planar, null);
        }

        if (!bool.TryParse(geodesicRaw, out var geodesic))
        {
            return new MeasurementModeReadResult(MeasurementMode.Planar, "invalid input 'geodesic'; expected boolean");
        }

        return new MeasurementModeReadResult(geodesic ? MeasurementMode.Geodesic : MeasurementMode.Planar, null);
    }

    private static MeasurementUnitReadResult ReadMeasurementUnit(
        IReadOnlyDictionary<string, string> parameters,
        string prefix,
        MeasurementKind kind)
    {
        var unitRaw = parameters.TryGetValue(prefix + "unit", out var rawUnit) ? rawUnit : null;
        if (kind == MeasurementKind.Area)
        {
            return GeodesicGeometryOperations.TryNormalizeAreaUnit(unitRaw, out var areaUnit, out var areaFactor)
                ? new MeasurementUnitReadResult(areaUnit, areaFactor, null)
                : new MeasurementUnitReadResult(
                    string.Empty,
                    0,
                    $"invalid input 'unit' '{unitRaw}'; expected square-meters, square-kilometers, hectares, acres, square-feet, or square-miles");
        }

        return GeodesicGeometryOperations.TryNormalizeLinearUnit(unitRaw, out var linearUnit, out var linearFactor)
            ? new MeasurementUnitReadResult(linearUnit, linearFactor, null)
            : new MeasurementUnitReadResult(
                string.Empty,
                0,
                $"invalid input 'unit' '{unitRaw}'; expected meters, kilometers, feet, yards, miles, or nautical-miles");
    }

    private static Base64WkbDecodeResult DecodeBase64Wkb(string wkb)
    {
        byte[] wkbBytes;
        try
        {
            wkbBytes = Convert.FromBase64String(wkb);
        }
        catch (FormatException)
        {
            return new Base64WkbDecodeResult([], "input 'wkb' is not valid base64");
        }

        return wkbBytes.Length == 0
            ? new Base64WkbDecodeResult([], "input 'wkb' decoded to zero bytes")
            : new Base64WkbDecodeResult(wkbBytes, null);
    }
}
