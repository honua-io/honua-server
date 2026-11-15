// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Infrastructure.Geometries;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Services;

internal sealed class GeometryConverter : IGeometryConverter
{
    // GeometryConverter is registered scoped (per-request) and its methods are
    // called sequentially within a request, so a single reader/writer pair per
    // instance is safe and avoids allocating a new WKBReader/GeoJsonWriter on
    // every conversion call (PA-101).
    private readonly WKBReader _wkbReader = new();
    private readonly GeoJsonWriter _geoJsonWriter = new();
    private readonly int _maxCurveVertices;

    public GeometryConverter(IOptions<LimitsOptions>? limitsOptions = null)
    {
        _maxCurveVertices = GeoServicesGeometryConverter.ResolveCurveVertexBudget(
            limitsOptions?.Value.Geometry.MaxVerticesPerGeometry);
    }

    public byte[] ConvertGeoServicesJsonToWkb(string geoServicesJsonGeometry)
    {
        try
        {
            var geometry = JsonSerializer.Deserialize(
                geoServicesJsonGeometry,
                FeatureServerJsonContext.Default.GeoServicesGeometry)
                ?? throw new ArgumentException("Invalid GeoServices JSON geometry format.");

            return GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(
                geometry,
                maxCurveVertices: _maxCurveVertices);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Invalid JSON format in geometry parameter");
        }
    }

    public object? ConvertWkbToGeoJson(byte[] wkbGeometry)
    {
        if (wkbGeometry == null || wkbGeometry.Length == 0)
            return null;

        try
        {
            var geometry = _wkbReader.Read(wkbGeometry);

            if (geometry == null)
                return null;

            var geoJsonString = RingWindingNormalizer.WriteGeoJson(_geoJsonWriter, geometry);

            // Return the GeoJSON string directly to avoid JsonElement AOT issues
            // The caller can parse this as needed for their specific JSON context
            return geoJsonString;
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            throw new ArgumentException("Invalid WKB geometry format.", ex);
        }
    }

    public object? ConvertWkbToGeoJson(Memory<byte> wkbGeometry)
    {
        if (wkbGeometry.Length == 0)
            return null;

        try
        {
            var geometry = _wkbReader.Read(wkbGeometry.Span.ToArray());

            if (geometry == null)
                return null;

            var geoJsonString = RingWindingNormalizer.WriteGeoJson(_geoJsonWriter, geometry);

            return geoJsonString;
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            throw new ArgumentException("Invalid WKB geometry format.", ex);
        }
    }

    public JsonElement ConvertWkbToGeoServicesGeometry(byte[] wkbGeometry, int srid)
    {
        ArgumentNullException.ThrowIfNull(wkbGeometry);

        var geoServicesGeometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(wkbGeometry, srid)
            ?? throw new ArgumentException("Failed to convert WKB to GeoServices geometry.");

        var json = JsonSerializer.Serialize(geoServicesGeometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

}
