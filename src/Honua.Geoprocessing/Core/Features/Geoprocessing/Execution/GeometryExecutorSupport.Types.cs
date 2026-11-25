// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

internal readonly record struct SingleGeometryInputs(byte[] WkbBytes, int Srid);

internal enum MeasurementMode
{
    Planar,
    Geodesic,
}

internal readonly record struct MeasuredGeometryInputs
{
    public required byte[] WkbBytes { get; init; }

    public required int Srid { get; init; }

    public required MeasurementMode Mode { get; init; }

    public required string Unit { get; init; }

    public required double MetersPerUnit { get; init; }

    public bool IsGeodesic => Mode == MeasurementMode.Geodesic;
}

internal readonly record struct PairGeometryInputs(string FirstWkb, string SecondWkb, int Srid);

internal readonly record struct PairGeometryInputNames(string FirstInputName, string SecondInputName);

internal readonly record struct SingleGeometryInputReadResult(SingleGeometryInputs Inputs, string? Error)
{
    public bool IsValid => Error is null;
}

internal readonly record struct MeasuredGeometryInputReadResult(MeasuredGeometryInputs Inputs, string? Error)
{
    public bool IsValid => Error is null;
}

internal readonly record struct PairGeometryInputReadResult(PairGeometryInputs Inputs, string? Error)
{
    public bool IsValid => Error is null;
}

internal readonly record struct GeometryDecodeResult(Geometry? Geometry, string? Error)
{
    public bool IsValid => Geometry is not null && Error is null;
}

internal enum WkbDecodeFailureKind
{
    None,
    InvalidWkb,
    EmptyGeometry,
    InvalidSrid,
}

internal readonly record struct ValidatedGeometryDecodeResult
{
    public Geometry? Geometry { get; init; }

    public WkbDecodeFailureKind FailureKind { get; init; }

    public string? Error { get; init; }

    public bool IsValid => FailureKind == WkbDecodeFailureKind.None && Geometry is not null;
}

internal readonly record struct MeasurePayloadRequest
{
    public required string ProcessId { get; init; }

    public required string Measure { get; init; }

    public required double Value { get; init; }

    public required string Unit { get; init; }

    public required int InputSrid { get; init; }

    public required string InputGeometryType { get; init; }
}

internal readonly record struct ValidatedGeometryPair
{
    public required Geometry First { get; init; }

    public required Geometry Second { get; init; }

    public required int Srid { get; init; }
}

internal readonly record struct ValidatedGeometryPairResult
{
    public ValidatedGeometryPair Pair { get; init; }

    public string? FailedField { get; init; }

    public string? Error { get; init; }

    public string? LogReason { get; init; }

    public bool IsValid => Error is null;
}

internal static partial class GeometryExecutorSupport
{
    public static ValidatedGeometryPairResult ReadAndDecodeValidatedPair(
        IReadOnlyDictionary<string, string> parameters,
        PairGeometryInputNames inputNames)
    {
        var inputRead = ReadPairGeometryInputs(parameters, inputNames);
        if (!inputRead.IsValid)
        {
            return new ValidatedGeometryPairResult { Error = inputRead.Error, LogReason = inputRead.Error };
        }

        var inputs = inputRead.Inputs;
        var firstDecode = DecodeAndValidatePairMember(inputs.FirstWkb, inputNames.FirstInputName, inputs.Srid);
        if (!firstDecode.IsValid)
        {
            return firstDecode;
        }

        var secondDecode = DecodeAndValidatePairMember(inputs.SecondWkb, inputNames.SecondInputName, inputs.Srid);
        if (!secondDecode.IsValid)
        {
            return secondDecode;
        }

        var pair = new ValidatedGeometryPair
        {
            First = firstDecode.Pair.First,
            Second = secondDecode.Pair.First,
            Srid = inputs.Srid,
        };
        return new ValidatedGeometryPairResult { Pair = pair };
    }

    private static ValidatedGeometryPairResult DecodeAndValidatePairMember(
        string base64Wkb,
        string fieldName,
        int srid)
    {
        var decode = DecodeGeometry(base64Wkb);
        if (!decode.IsValid)
        {
            return new ValidatedGeometryPairResult
            {
                FailedField = fieldName,
                Error = $"{fieldName} {decode.Error}",
                LogReason = decode.Error,
            };
        }

        var geometry = decode.Geometry!;
        if (!GeometrySridGuard.TryValidateEmbeddedSrid(geometry, srid, out var sridError))
        {
            return new ValidatedGeometryPairResult
            {
                FailedField = fieldName,
                Error = $"{fieldName} {sridError}.",
                LogReason = sridError,
            };
        }

        geometry.SRID = srid;
        return new ValidatedGeometryPairResult
        {
            Pair = new ValidatedGeometryPair { First = geometry, Second = geometry, Srid = srid },
        };
    }
}
