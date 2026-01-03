// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.FeatureServer.Services.QueryBuilding;

/// <summary>
/// Standard implementation of feature query builder for typical GeoServices REST queries.
/// Handles spatial filters, object ID filters, field selection, and ordering.
/// </summary>
internal sealed class StandardFeatureQueryBuilder : IFeatureQueryBuilder
{
    /// <summary>
    /// Builds a standard feature query from the provided context
    /// </summary>
    public FeatureQuery BuildQuery(QueryBuildingContext context)
    {
        var hasObjectIds = context.QueryParams.ObjectIds is { Length: > 0 };
        var effectiveSqlFilter = hasObjectIds ? null : context.SqlFilter;
        var effectiveWhere = hasObjectIds ? null : context.QueryParams.Where;

        var query = new FeatureQuery
        {
            Where = effectiveWhere,
            SqlFilter = effectiveSqlFilter,
            ObjectIds = hasObjectIds ? context.QueryParams.ObjectIds?.ToImmutableArray() : null,
            Offset = context.QueryParams.ResultOffset,
            Limit = context.QueryParams.ResultRecordCount ?? context.Service.MaxRecordCount,
            SpatialReferenceSrid = context.Layer.SpatialReference.Srid,
            OutputSrid = context.OutputSrid,
            OrderBy = ParseOrderByFields(context.QueryParams.OrderByFields, context.Layer)
        };

        // Apply field selection
        query = ApplyFieldSelection(query, context.QueryParams.OutFields);

        // Apply spatial filter if geometry or nearest count is specified
        query = ApplySpatialFilter(query, context);

        return query;
    }

    private static FeatureQuery ApplyFieldSelection(FeatureQuery query, string? outFields)
    {
        if (string.IsNullOrEmpty(outFields))
        {
            return query;
        }

        if (outFields == "*")
        {
            // Return all fields - no filtering needed
            return query with { OutFields = null };
        }

        var fields = outFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToImmutableArray();

        return query with { OutFields = fields };
    }

    private static FeatureQuery ApplySpatialFilter(FeatureQuery query, QueryBuildingContext context)
    {
        if (context.ParsedGeometry == null && !context.QueryParams.NearestCount.HasValue)
        {
            return query;
        }

        try
        {
            // For KNN queries without explicit geometry, geometry is required
            if (context.QueryParams.NearestCount.HasValue && context.ParsedGeometry == null)
            {
                throw new InvalidOperationException("Geometry is required for nearest neighbor queries");
            }

            var spatialFilter = BuildSpatialFilter(context);
            return query with { SpatialFilter = spatialFilter };
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid spatial parameters: {ex.Message}");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Invalid geometry: {ex.Message}");
        }
    }

    private static SpatialFilter BuildSpatialFilter(QueryBuildingContext context)
    {
        var geometry = context.ParsedGeometry!;
        var queryParams = context.QueryParams;

        // Convert GeoServices JSON geometry to WKB bytes
        byte[] wkbBytes = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry, context.InputSrid);

        // Check if this is a KNN query
        if (queryParams.NearestCount.HasValue && queryParams.NearestCount.Value > 0)
        {
            return SpatialFilter.CreateKnnFilter(
                wkbBytes,
                queryParams.NearestCount.Value,
                queryParams.ReturnDistance,
                context.InputSrid);
        }

        // Parse spatial relationship
        SpatialRelationship relationship = ParseSpatialRelationship(queryParams.SpatialRel);

        // Handle distance-based queries
        if (relationship == SpatialRelationship.WithinDistance ||
            relationship == SpatialRelationship.BeyondDistance)
        {
            if (!queryParams.Distance.HasValue || queryParams.Distance.Value <= 0)
            {
                throw new ArgumentException("Distance parameter is required for distance-based spatial queries");
            }

            var unit = ParseDistanceUnit(queryParams.Units);
            return SpatialFilter.CreateDistanceFilter(
                wkbBytes,
                queryParams.Distance.Value,
                unit,
                relationship == SpatialRelationship.WithinDistance,
                context.InputSrid);
        }

        return new SpatialFilter
        {
            Geometry = wkbBytes,
            SpatialRelationship = relationship,
            Srid = context.InputSrid
        };
    }

    private static SpatialRelationship ParseSpatialRelationship(string? spatialRel)
    {
        return spatialRel?.ToLowerInvariant() switch
        {
            "esrispatialrelintersects" or null => SpatialRelationship.Intersects,
            "esrispatialrelcontains" => SpatialRelationship.Contains,
            "esrispatialrelwithin" => SpatialRelationship.Within,
            "esrispatialrelenvelopeintersects" => SpatialRelationship.EnvelopeIntersects,
            "esrispatialrelcrosses" => SpatialRelationship.Crosses,
            "esrispatialreltouches" => SpatialRelationship.Touches,
            "esrispatialreloverlaps" => SpatialRelationship.Overlaps,
            "esrispatialreldisjoint" => SpatialRelationship.Disjoint,
            "esrispatialrelequals" => SpatialRelationship.Equals,
            "esrispatialrelwithindistance" => SpatialRelationship.WithinDistance,
            "esrispatialrelbeyonddistance" => SpatialRelationship.BeyondDistance,
            _ => throw new ArgumentException($"Unsupported spatial relationship: {spatialRel}")
        };
    }

    private static DistanceUnit ParseDistanceUnit(string? units)
    {
        return units?.ToLowerInvariant() switch
        {
            "esrisrunit_meter" or null => DistanceUnit.Meters,
            "esrisrunit_foot" => DistanceUnit.Feet,
            "esrisrunit_kilometer" => DistanceUnit.Kilometers,
            "esrisrunit_statutemile" => DistanceUnit.Miles,
            // Also support simple unit names
            "meters" or "m" => DistanceUnit.Meters,
            "feet" or "ft" => DistanceUnit.Feet,
            "kilometers" or "km" => DistanceUnit.Kilometers,
            "miles" or "mi" => DistanceUnit.Miles,
            _ => DistanceUnit.Meters // Default to meters for unknown units
        };
    }

    private static ImmutableArray<OrderByClause>? ParseOrderByFields(string? orderByFields, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(orderByFields))
        {
            return null;
        }

        var clauses = new List<OrderByClause>();
        foreach (var rawField in orderByFields.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawField.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var field = parts[0];
            var ascending = true;

            if (parts.Length > 1)
            {
                ascending = !parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase);
            }

            var fieldDefinition = layer.Fields.FirstOrDefault(f =>
                f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
            var resolvedField = fieldDefinition?.Name ?? field;
            var fieldType = fieldDefinition?.Type;

            clauses.Add(new OrderByClause(resolvedField, ascending, fieldType));
        }

        return clauses.Count == 0 ? null : clauses.ToImmutableArray();
    }
}
