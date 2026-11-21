// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Geometries;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Validation;

internal sealed class FeatureMutationValidator
{
    private readonly IGeometryValidator _geometryValidator;

    public FeatureMutationValidator(IGeometryValidator geometryValidator)
    {
        _geometryValidator = geometryValidator ?? throw new ArgumentNullException(nameof(geometryValidator));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as an instance method for the validator service API.")]
    public ValidationResult<ImmutableDictionary<string, object?>> ValidateAttributes(
        MetadataV2Object resource,
        IReadOnlyDictionary<string, object?>? attributes,
        ValidationExtensions.AttributeValidationMode mode,
        bool isUpdate = false)
    {
        return resource.ValidateAttributesV2(attributes, mode, isUpdate);
    }

    public Task<GeometryMutationResult> ValidateGeometryAsync(
        MetadataV2Object resource,
        byte[]? geometry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var layerSrid = resource.ReadSrid();
        return ValidateGeometryAsync(
            geometry,
            ResolveLayerGeometryType(resource),
            layerSrid is int srid && GeographicSridClassifier.IsGeographicSrid(srid) ? srid : null,
            cancellationToken);
    }

    // None does not constrain the submitted geometry's shape.
    public Task<GeometryMutationResult> ValidateGeometryAsync(
        byte[]? geometry,
        MetadataV2GeometryType layerGeometryType,
        CancellationToken cancellationToken)
        => ValidateGeometryAsync(geometry, layerGeometryType, geographicSrid: null, cancellationToken);

    private async Task<GeometryMutationResult> ValidateGeometryAsync(
        byte[]? geometry,
        MetadataV2GeometryType layerGeometryType,
        int? geographicSrid,
        CancellationToken cancellationToken)
    {
        if (geometry == null || geometry.Length == 0)
        {
            return GeometryMutationResult.Success(geometry);
        }

        var validationResult = await _geometryValidator.ValidateCompleteAsync(geometry, cancellationToken);
        if (!validationResult.IsValid)
        {
            var errorMessages = string.Join("; ", validationResult.Errors.Select(error => error.Message));
            return GeometryMutationResult.Failure(errorMessages);
        }

        var finalGeometry = validationResult.WasRepaired
            ? validationResult.RepairedWkb
            : geometry;
        if (geographicSrid is int layerSrid && finalGeometry is { Length: > 0 })
        {
            var cut = CutAntimeridian(finalGeometry, layerSrid);
            if (!ReferenceEquals(cut, finalGeometry))
            {
                // The cut adds vertices and can split one ring into several polygons
                // after ValidateCompleteAsync has already counted the input.
                var limitResult = _geometryValidator.ValidateWkb(cut);
                if (!limitResult.IsValid)
                {
                    var limitErrors = string.Join("; ", limitResult.Errors.Select(error => error.Message));
                    return GeometryMutationResult.Failure(limitErrors);
                }

                finalGeometry = cut;
            }
        }

        return GeometryTypeEnforcement.Enforce(
            geometry,
            finalGeometry,
            layerGeometryType,
            validationResult.WasRepaired,
            validationResult.GeometryTypeChanged);
    }

    public static MetadataV2GeometryType ResolveLayerGeometryType(MetadataV2Object resource)
        => resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;

    private static byte[] CutAntimeridian(byte[] wkb, int layerSrid)
    {
        NetTopologySuite.Geometries.Geometry geometry;
        try
        {
            geometry = new WKBReader().Read(wkb);
        }
        catch (Exception ex) when (ex is ArgumentException or ParseException
            or IOException or InvalidOperationException or IndexOutOfRangeException)
        {
            return wkb;
        }

        if (geometry.SRID <= 0)
        {
            geometry.SRID = layerSrid;
        }

        var cut = AntimeridianCutter.Cut(geometry);
        if (ReferenceEquals(cut, geometry))
        {
            return wkb;
        }

        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: cut.SRID > 0);
        return writer.Write(cut);
    }
}

internal static class GeometryTypeEnforcement
{
    public static GeometryMutationResult Enforce(
        byte[] inputGeometry,
        byte[]? finalGeometry,
        MetadataV2GeometryType layerGeometryType,
        bool wasRepaired,
        bool repairChangedType)
    {
        var expectedFamily = GetLayerFamily(layerGeometryType);
        if (expectedFamily is null || finalGeometry is null || finalGeometry.Length == 0)
        {
            return GeometryMutationResult.Success(finalGeometry);
        }

        NetTopologySuite.Geometries.Geometry parsed;
        try
        {
            parsed = new NetTopologySuite.IO.WKBReader().Read(finalGeometry);
        }
        catch (Exception ex) when (ex is ArgumentException or NetTopologySuite.IO.ParseException
            or IOException or InvalidOperationException or IndexOutOfRangeException)
        {
            return GeometryMutationResult.Failure("Geometry could not be read to verify the layer geometry type.");
        }

        var actualFamily = GetFamily(parsed.OgcGeometryType);
        if (actualFamily == expectedFamily)
        {
            var promoted = (layerGeometryType, parsed) switch
            {
                (MetadataV2GeometryType.MultiPoint, NetTopologySuite.Geometries.Point point)
                    => (NetTopologySuite.Geometries.Geometry)parsed.Factory.CreateMultiPoint([point]),
                (MetadataV2GeometryType.MultiLineString, NetTopologySuite.Geometries.LineString line)
                    => parsed.Factory.CreateMultiLineString([line]),
                (MetadataV2GeometryType.MultiPolygon, NetTopologySuite.Geometries.Polygon polygon)
                    => parsed.Factory.CreateMultiPolygon([polygon]),
                _ => null
            };
            if (promoted is null)
            {
                return GeometryMutationResult.Success(finalGeometry);
            }

            promoted.SRID = parsed.SRID;
            return GeometryMutationResult.Success(WriteWkb(promoted, finalGeometry));
        }

        var layerDescription = layerGeometryType.ToString();
        if (wasRepaired)
        {
            if (parsed.OgcGeometryType == NetTopologySuite.Geometries.OgcGeometryType.GeometryCollection &&
                TryExtractFamily(parsed, expectedFamily.Value, layerGeometryType, out var extracted))
            {
                return GeometryMutationResult.Success(WriteWkb(extracted));
            }

            var originalType = TryReadGeometryType(inputGeometry) ?? "the submitted type";
            return GeometryMutationResult.Failure(repairChangedType
                ? $"Geometry repair changed the geometry type from {originalType} to {parsed.GeometryType}, which does not match the layer geometry type {layerDescription}."
                : $"Repaired geometry type {parsed.GeometryType} does not match the layer geometry type {layerDescription}.");
        }

        return GeometryMutationResult.Failure(
            $"Geometry type {parsed.GeometryType} does not match the layer geometry type {layerDescription}.");
    }

    private enum GeometryFamily
    {
        Point = 0,
        Line = 1,
        Polygon = 2
    }

    private static GeometryFamily? GetLayerFamily(MetadataV2GeometryType layerGeometryType)
        => layerGeometryType switch
        {
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint => GeometryFamily.Point,
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => GeometryFamily.Line,
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => GeometryFamily.Polygon,
            // None / Mixed / GeometryCollection layers do not constrain the geometry shape.
            _ => null
        };

    private static GeometryFamily? GetFamily(NetTopologySuite.Geometries.OgcGeometryType type)
        => type switch
        {
            NetTopologySuite.Geometries.OgcGeometryType.Point or NetTopologySuite.Geometries.OgcGeometryType.MultiPoint
                => GeometryFamily.Point,
            NetTopologySuite.Geometries.OgcGeometryType.LineString or NetTopologySuite.Geometries.OgcGeometryType.MultiLineString
                => GeometryFamily.Line,
            NetTopologySuite.Geometries.OgcGeometryType.Polygon or NetTopologySuite.Geometries.OgcGeometryType.MultiPolygon
                => GeometryFamily.Polygon,
            _ => null
        };

    private static bool TryExtractFamily(
        NetTopologySuite.Geometries.Geometry collection,
        GeometryFamily expectedFamily,
        MetadataV2GeometryType layerGeometryType,
        out NetTopologySuite.Geometries.Geometry extracted)
    {
        extracted = collection;
        var matches = new List<NetTopologySuite.Geometries.Geometry>();
        var stack = new Stack<NetTopologySuite.Geometries.Geometry>();
        stack.Push(collection);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.IsEmpty)
            {
                continue;
            }

            if (current.OgcGeometryType is NetTopologySuite.Geometries.OgcGeometryType.GeometryCollection
                or NetTopologySuite.Geometries.OgcGeometryType.MultiPoint
                or NetTopologySuite.Geometries.OgcGeometryType.MultiLineString
                or NetTopologySuite.Geometries.OgcGeometryType.MultiPolygon)
            {
                for (var i = current.NumGeometries - 1; i >= 0; i--)
                {
                    stack.Push(current.GetGeometryN(i));
                }

                continue;
            }

            var family = GetFamily(current.OgcGeometryType);
            if (family is null || family.Value > expectedFamily)
            {
                // A component of a higher dimension (or an unknown type) makes the extraction
                // ambiguous: dropping it would silently discard part of the submitted shape.
                return false;
            }

            if (family.Value == expectedFamily)
            {
                matches.Add(current);
            }
        }

        if (matches.Count == 0)
        {
            return false;
        }

        var factory = collection.Factory;
        var wantsMulti = layerGeometryType is MetadataV2GeometryType.MultiPoint
            or MetadataV2GeometryType.MultiLineString
            or MetadataV2GeometryType.MultiPolygon;
        if (matches.Count == 1 && !wantsMulti)
        {
            extracted = matches[0];
        }
        else
        {
            extracted = expectedFamily switch
            {
                GeometryFamily.Point => factory.CreateMultiPoint(
                    matches.Cast<NetTopologySuite.Geometries.Point>().ToArray()),
                GeometryFamily.Line => factory.CreateMultiLineString(
                    matches.Cast<NetTopologySuite.Geometries.LineString>().ToArray()),
                _ => factory.CreateMultiPolygon(
                    matches.Cast<NetTopologySuite.Geometries.Polygon>().ToArray())
            };
        }

        extracted.SRID = collection.SRID;
        return true;
    }

    private static byte[] WriteWkb(NetTopologySuite.Geometries.Geometry geometry, byte[]? source = null)
    {
        // Empty geometries have no coordinates from which to infer dimensions.
        // Preserve the source WKB's EWKB flags or ISO dimensional type offset.
        var type = source is { Length: >= 5 }
            ? source[0] == 1
                ? BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(1, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(1, 4))
            : 0u;
        var dimension = (type & 0x1fffffffu) / 1000;
        var emitZ = (type & 0x80000000u) != 0 || dimension is 1 or 3;
        var emitM = (type & 0x40000000u) != 0 || dimension is 2 or 3;
        foreach (var coordinate in geometry.Coordinates)
        {
            emitZ |= !double.IsNaN(coordinate.Z);
            emitM |= !double.IsNaN(coordinate.M);
        }

        var writer = new NetTopologySuite.IO.WKBWriter(
            NetTopologySuite.IO.ByteOrder.LittleEndian,
            handleSRID: geometry.SRID > 0,
            emitZ: emitZ,
            emitM: emitM);
        return writer.Write(geometry);
    }

    private static string? TryReadGeometryType(byte[] wkb)
    {
        try
        {
            return new NetTopologySuite.IO.WKBReader().Read(wkb).GeometryType;
        }
        catch (Exception ex) when (ex is ArgumentException or NetTopologySuite.IO.ParseException
            or IOException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}

internal sealed record GeometryMutationResult
{
    public bool IsValid { get; init; }
    public byte[]? Geometry { get; init; }
    public string? ErrorMessage { get; init; }

    public static GeometryMutationResult Success(byte[]? geometry)
        => new() { IsValid = true, Geometry = geometry };

    public static GeometryMutationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}

internal static class MetadataV2AttributeValidation
{
    public static ValidationResult<ImmutableDictionary<string, object?>> ValidateAttributesV2(
        this MetadataV2Object resource,
        IReadOnlyDictionary<string, object?>? attributes,
        ValidationExtensions.AttributeValidationMode mode = ValidationExtensions.AttributeValidationMode.Strict,
        bool isUpdate = false)
    {
        var options = new AttributeValidationOptions(
            mode == ValidationExtensions.AttributeValidationMode.GeoServices
                ? AttributeValidationProfile.GeoServices : AttributeValidationProfile.Strict,
            isUpdate ? AttributeMutationOperation.Update : AttributeMutationOperation.Insert,
            // JSON and GeoServices clients have no exact wire type. gRPC stays strict.
            mode == ValidationExtensions.AttributeValidationMode.Strict
                ? ExactValueCoercion.None
                : ExactValueCoercion.CanonicalText);
        return FeatureAttributeValidator.Validate(resource, attributes, options);
    }
}
