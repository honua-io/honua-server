// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geometry.Services;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcGeoJsonGeometryShapeValidator
{
    public static GeometryShapeValidationResult GetValidationResult(JsonElement geometryElement, string type) =>
        (GeometryShapeValidationResult)GeoJsonGeometryShapeValidator.GetValidationResult(geometryElement, type);
    public static bool IsKnownValidGeometry(JsonElement element) => GeoJsonGeometryShapeValidator.IsKnownValidGeometry(element);
}

internal enum GeometryShapeValidationResult { UnknownType, Invalid, Valid }
