// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Core.Features.Scene.Bim;

/// <summary>
/// Converts a parsed <see cref="CityGmlModel"/> into a deterministic 3D Tiles
/// tileset with Building Scene Layer (BSL) semantics (#1207): one GLB tile whose
/// triangulated boundary surfaces carry per-feature BSL attributes (building id,
/// storey, surface type, discipline / sub-layer, and the parsed generic
/// attributes) through the existing <c>EXT_structural_metadata</c> path, plus a
/// servable <c>tileset.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each CityGML boundary surface (wall / roof / ground / ...) becomes one
/// <see cref="SceneFeature"/> polygon. The model's source coordinates are placed
/// on the WGS-84 ellipsoid by a caller-supplied <see cref="CityGmlGeoTransform"/>
/// (which interprets the document CRS), exactly mirroring how the point-cloud
/// pipeline projects LAS coordinates. The resulting features are handed to the
/// shared <see cref="GeometryTileBuilder"/> so geometry &#8594; ECEF projection,
/// triangulation, and the per-feature metadata table are reused rather than
/// reimplemented.
/// </para>
/// <para>
/// Output is byte-stable: features keep document order, BSL attribute columns
/// are emitted in a fixed schema order, and the GLB/tileset writers are
/// deterministic, so identical input produces identical bytes across runs.
/// </para>
/// </remarks>
public static class BuildingSceneLayerBuilder
{
    /// <summary>BSL attribute: the owning building's <c>gml:id</c>.</summary>
    public const string AttributeBuildingId = "bsl_building_id";

    /// <summary>BSL attribute: the owning building's name (when present).</summary>
    public const string AttributeBuildingName = "bsl_building_name";

    /// <summary>BSL attribute: the owning storey identifier (the BSL level sub-layer).</summary>
    public const string AttributeStoreyId = "bsl_storey_id";

    /// <summary>BSL attribute: the CityGML surface type (WallSurface, RoofSurface, ...).</summary>
    public const string AttributeSurfaceType = "bsl_surface_type";

    /// <summary>BSL attribute: the BIM discipline / Building Scene Layer sub-layer.</summary>
    public const string AttributeDiscipline = "bsl_discipline";

    /// <summary>BSL attribute: the boundary-surface component <c>gml:id</c>.</summary>
    public const string AttributeComponentId = "bsl_component_id";

    /// <summary>
    /// Builds the deterministic tileset artifacts (tileset.json bytes plus the
    /// GLB tile bytes) for a CityGML building model with BSL attributes.
    /// </summary>
    /// <param name="model">The parsed CityGML model. Must contain at least one surface.</param>
    /// <param name="transform">Projects a model (x, y, z) to (lon&#176;, lat&#176;, heightMeters), interpreting the document CRS.</param>
    /// <param name="generatorTag">Optional generator label for the tileset/GLB asset blocks.</param>
    /// <returns>The deterministic BSL tileset artifacts.</returns>
    /// <exception cref="ArgumentException">The model carries no boundary surfaces.</exception>
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
    {
        var lonSpanRad = (bounds[2] - bounds[0]) * Math.PI / 180.0;
        var latSpanRad = (bounds[3] - bounds[1]) * Math.PI / 180.0;
        var lonMeters = Math.Abs(lonSpanRad) * EcefCoordinateTransform.WgsSemiMajorAxis
            * Math.Cos((bounds[1] + bounds[3]) * 0.5 * Math.PI / 180.0);
        var latMeters = Math.Abs(latSpanRad) * EcefCoordinateTransform.WgsSemiMajorAxis;
        var heightMeters = Math.Max(0.0, maxHeight - minHeight);
        var diagonal = Math.Sqrt(lonMeters * lonMeters + latMeters * latMeters + heightMeters * heightMeters);
        return Math.Round(diagonal, 6, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Projects a CityGML model coordinate to (longitude&#176;, latitude&#176;,
/// height m). Implementations interpret the document CRS (geographic documents
/// pass through; projected documents apply the inverse projection).
/// </summary>
/// <param name="x">Model X in the document's horizontal units.</param>
/// <param name="y">Model Y in the document's horizontal units.</param>
/// <param name="z">Model Z (elevation) in meters.</param>
/// <returns>Geographic longitude/latitude in degrees and ellipsoidal height in meters.</returns>
public delegate (double Longitude, double Latitude, double Height) CityGmlGeoTransform(double x, double y, double z);

/// <summary>
/// Deterministic artifacts produced from a CityGML building model (#1207): the
/// serialized <c>tileset.json</c> bytes, the GLB tile bytes, and BSL summary
/// fields (building / surface counts and the discovered discipline sub-layers).
/// </summary>
/// <param name="TilesetJsonBytes">Serialized tileset document.</param>
/// <param name="Tiles">Map of relative GLB uri to tile bytes.</param>
/// <param name="BoundsDegrees">Dataset envelope [west, south, east, north] in degrees.</param>
/// <param name="BuildingCount">Number of buildings ingested.</param>
/// <param name="SurfaceCount">Number of boundary-surface components emitted.</param>
/// <param name="Disciplines">Distinct BSL disciplines / sub-layers present, in sorted order.</param>
/// <param name="Warnings">Non-fatal coercion warnings raised by the metadata writer.</param>
public readonly record struct BuildingSceneLayerResult(
    byte[] TilesetJsonBytes,
    IReadOnlyDictionary<string, byte[]> Tiles,
    double[] BoundsDegrees,
    int BuildingCount,
    int SurfaceCount,
    IReadOnlyList<string> Disciplines,
    IReadOnlyList<string> Warnings);
