// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Core.Features.Scene.Bim;

public static class BuildingSceneLayerBuilder
{
    public const string AttributeBuildingId = "bsl_building_id";

    public const string AttributeBuildingName = "bsl_building_name";

    public const string AttributeStoreyId = "bsl_storey_id";

    public const string AttributeSurfaceType = "bsl_surface_type";

    public const string AttributeDiscipline = "bsl_discipline";

    public const string AttributeComponentId = "bsl_component_id";

    public static BuildingSceneLayerResult Build(
        CityGmlModel model,
        CityGmlGeoTransform transform,
        string? generatorTag = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(transform);

        // 1. Flatten the BSL hierarchy into ordered polygon features, projecting
        //    every vertex once and tracking the geographic envelope + the set of
        //    generic attribute keys that need a metadata column.
        var features = new List<SceneFeature>();
        var genericKeys = new List<string>();
        var seenGenericKeys = new HashSet<string>(StringComparer.Ordinal);

        var west = double.PositiveInfinity;
        var south = double.PositiveInfinity;
        var east = double.NegativeInfinity;
        var north = double.NegativeInfinity;
        var minHeight = double.PositiveInfinity;
        var maxHeight = double.NegativeInfinity;

        long featureId = 0;
        var buildingCount = 0;
        var surfaceCount = 0;
        var disciplines = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var building in model.Buildings)
        {
            buildingCount++;
            foreach (var storey in building.Storeys)
            {
                foreach (var surface in storey.Surfaces)
                {
                    if (surface.Ring.Count < 3)
                    {
                        continue;
                    }

                    var vertices = new List<SceneVertex>(surface.Ring.Count);
                    foreach (var v in surface.Ring)
                    {
                        var (lon, lat, height) = transform(v.X, v.Y, v.Z);
                        vertices.Add(new SceneVertex(lon, lat, height));

                        if (lon < west) west = lon;
                        if (lat < south) south = lat;
                        if (lon > east) east = lon;
                        if (lat > north) north = lat;
                        if (height < minHeight) minHeight = height;
                        if (height > maxHeight) maxHeight = height;
                    }

                    var attributes = BuildAttributeBag(
                        building, storey, surface, genericKeys, seenGenericKeys);

                    features.Add(new SceneFeature
                    {
                        Id = featureId++,
                        Geometry = new SceneFeatureGeometry
                        {
                            Kind = SceneGeometryKind.Polygon,
                            Vertices = vertices
                        },
                        Attributes = attributes
                    });
                    surfaceCount++;
                    disciplines.Add(surface.Discipline);
                }
            }
        }

        if (features.Count == 0)
        {
            throw new ArgumentException(
                "CityGML model contains no boundary surfaces with at least three vertices.",
                nameof(model));
        }

        // 2. Build the metadata schema: fixed BSL columns first (stable order),
        //    then a STRING column per discovered generic attribute. The keys are
        //    discovered by enumerating Dictionary<string,string>, whose
        //    enumeration order is unspecified by the BCL, so sort them with an
        //    explicit ordinal comparison to make the column layout (and thus the
        //    GLB buffer bytes) byte-identical across runs independent of
        //    dictionary internals.
        genericKeys.Sort(StringComparer.Ordinal);
        var schemas = BuildSchemas(genericKeys);

        // 3. Hand off to the shared GLB writer (geometry -> ECEF, triangulation,
        //    EXT_structural_metadata table) and the shared tileset writer.
        var warnings = new List<string>();
        var glb = GeometryTileBuilder.BuildGlb(
            features,
            schemas,
            extrusion: null,
            generatorTag,
            warnings);

        var bounds = new[] { west, south, east, north };
        if (double.IsPositiveInfinity(minHeight)) minHeight = 0.0;
        if (double.IsNegativeInfinity(maxHeight)) maxHeight = 0.0;
        var geometricError = ComputeGeometricError(bounds, minHeight, maxHeight);

        const string tileUri = "building_0000.glb";
        var tileset = TilesetDocumentWriter.Build(
            bounds,
            minHeight,
            maxHeight,
            geometricError,
            tileContentUris: [tileUri],
            generatorTag);
        var tilesetBytes = TilesetDocumentWriter.Serialize(tileset);

        var tiles = new Dictionary<string, byte[]>(1, StringComparer.Ordinal) { [tileUri] = glb };

        return new BuildingSceneLayerResult(
            tilesetBytes,
            tiles,
            bounds,
            buildingCount,
            surfaceCount,
            disciplines.ToArray(),
            warnings.AsReadOnly());
    }

    private static Dictionary<string, object?> BuildAttributeBag(
        CityGmlBuilding building,
        CityGmlStorey storey,
        CityGmlBoundarySurface surface,
        List<string> genericKeys,
        HashSet<string> seenGenericKeys)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [AttributeBuildingId] = building.Id,
            [AttributeBuildingName] = building.Name ?? string.Empty,
            [AttributeStoreyId] = storey.Id,
            [AttributeSurfaceType] = surface.SurfaceType,
            [AttributeDiscipline] = surface.Discipline,
            [AttributeComponentId] = surface.Id
        };

        // Building-level generic attributes apply to every component; surface
        // attributes override on collision (most specific wins).
        foreach (var (key, value) in building.Attributes)
        {
            var column = GenericColumnName(key);
            bag[column] = value;
            TrackGenericKey(column, genericKeys, seenGenericKeys);
        }
        foreach (var (key, value) in surface.Attributes)
        {
            var column = GenericColumnName(key);
            bag[column] = value;
            TrackGenericKey(column, genericKeys, seenGenericKeys);
        }

        return bag;
    }

    private static void TrackGenericKey(string column, List<string> genericKeys, HashSet<string> seen)
    {
        if (seen.Add(column))
        {
            genericKeys.Add(column);
        }
    }

    private static string GenericColumnName(string key) => "attr_" + key;

    private static List<SceneAttributeSchema> BuildSchemas(List<string> genericKeys)
    {
        var schemas = new List<SceneAttributeSchema>(6 + genericKeys.Count)
        {
            StringSchema(AttributeBuildingId),
            StringSchema(AttributeBuildingName),
            StringSchema(AttributeStoreyId),
            StringSchema(AttributeSurfaceType),
            StringSchema(AttributeDiscipline),
            StringSchema(AttributeComponentId)
        };
        foreach (var key in genericKeys)
        {
            schemas.Add(StringSchema(key));
        }
        return schemas;
    }

    private static SceneAttributeSchema StringSchema(string fieldName) => new()
    {
        PropertyId = fieldName,
        FieldName = fieldName,
        SchemaType = "STRING",
        SchemaComponentType = string.Empty
    };

    private static double ComputeGeometricError(double[] bounds, double minHeight, double maxHeight)
        // Shared cos-lat-corrected diagonal geodesy (see GeodesicError). This now
        // also applies the positive floor the point-cloud builder uses, so a
        // degenerate single-building extent declares a non-zero refinement budget.
        => GeodesicError.RootGeometricError(bounds, minHeight, maxHeight);
}

public delegate (double Longitude, double Latitude, double Height) CityGmlGeoTransform(double x, double y, double z);

public readonly record struct BuildingSceneLayerResult(
    byte[] TilesetJsonBytes,
    IReadOnlyDictionary<string, byte[]> Tiles,
    double[] BoundsDegrees,
    int BuildingCount,
    int SurfaceCount,
    IReadOnlyList<string> Disciplines,
    IReadOnlyList<string> Warnings);
