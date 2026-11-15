// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.FileImport.Services;

internal sealed partial class StreamingGeoJsonReader
{
    private static void RetainValidationIssue(List<ImportValidationIssue> issues, ImportValidationIssue issue)
    {
        if (issues.Count < MaxValidationIssues)
        {
            issues.Add(issue);
        }
        else if (issue.Severity == ImportValidationSeverity.Error &&
            issues.All(existing => existing.Severity != ImportValidationSeverity.Error))
        {
            // A bounded warning list must never conceal a later fatal error.
            issues[^1] = issue;
        }
    }

    private bool HasCompleteGeometryComponents(JsonElement geometry)
    {
        if (geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !_knownGeometryTypes.Contains(type.GetString()!))
        {
            return false;
        }

        if (type.GetString() == "GeometryCollection")
        {
            return geometry.TryGetProperty("geometries", out var children) &&
                children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0 &&
                children.EnumerateArray().All(HasCompleteGeometryComponents);
        }

        if (!geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var complete = type.GetString() switch
        {
            "MultiLineString" => coordinates.EnumerateArray().All(line => HasMinimumPositions(line, 2)),
            "Polygon" => HasCompletePolygonRings(coordinates),
            "MultiPolygon" => coordinates.EnumerateArray().All(HasCompletePolygonRings),
            _ => true
        };
        // Collection parsing filters null children. Validate every leaf independently
        // so malformed coordinates or rings cannot disappear from a valid collection.
        return complete && ParseGeometry(geometry) is { IsEmpty: false };
    }

    private static bool HasCompletePolygonRings(JsonElement polygon) =>
        polygon.ValueKind == JsonValueKind.Array && polygon.GetArrayLength() > 0 &&
        polygon.EnumerateArray().All(ring => HasMinimumPositions(ring, 4));

    private static bool HasMinimumPositions(JsonElement positions, int minimum) =>
        positions.ValueKind == JsonValueKind.Array && positions.GetArrayLength() >= minimum;

}
