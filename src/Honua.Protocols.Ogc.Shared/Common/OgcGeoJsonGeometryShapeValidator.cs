// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
