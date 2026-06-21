// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// CNG conformance artifact generator.
//
// Drives honua's own cloud-native writers to emit the artifacts that the lane
// then validates with the canonical first-party tools:
//   - GeoParquet 1.1.0      <- GeoParquetFeatureWriter   (-> `gpq validate`)
//   - PMTiles v3 archive    <- PMTilesWriter             (-> `pmtiles verify`)
//   - 3D Tiles tileset.json <- TilesetDocumentWriter     (-> `3d-tiles-validator`)
//   - glTF 2.0 GLB content  <- GeometryTileBuilder        (-> `gltf_validator`)
//
// GeoParquet here additionally cross-checks the same writer the live
// FeatureServer `f=parquet` path uses, so the lane can compare the offline and
// HTTP encodings if needed; the lane's primary GeoParquet/FlatGeobuf evidence is
// still fetched live from the seeded store-backed FeatureServer.
//
// Usage: dotnet run --project <this> -- <output-directory>
// Emits: <out>/honua.parquet, <out>/honua.pmtiles,
//        <out>/3dtiles/tileset.json, <out>/3dtiles/content.glb

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Infrastructure.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

var outDir = args.Length > 0 ? args[0] : "cng-artifacts";
Directory.CreateDirectory(outDir);

GenerateGeoParquet(Path.Combine(outDir, "honua.parquet"));
await GeneratePMTilesAsync(Path.Combine(outDir, "honua.pmtiles")).ConfigureAwait(false);
GenerateThreeDTiles(Path.Combine(outDir, "3dtiles"));

Console.WriteLine($"CNG artifacts written to {Path.GetFullPath(outDir)}");
return 0;

// --- GeoParquet 1.1.0 -----------------------------------------------------

static void GenerateGeoParquet(string path)
{
    // Drive honua's shared GeoParquet encoder (GeoParquetFeatureWriter, via the
    // GeoServices formatter adapter) over a small canonical feature set so
    // `gpq validate` checks the actual `geo` metadata, WKB encoding and CRS that
    // honua emits — the same writer the FeatureServer `f=parquet` path uses.
    var resource = new MetadataV2Resource
    {
        Metadata = new MetadataV2ObjectMetadata
        {
            Id = "cng-layer",
            Name = "cng_features",
            Description = "CNG conformance layer",
        },
        SchemaFields =
        [
            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 255, Nullable = true },
            new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer, Nullable = true },
            new MetadataV2Field { Name = "ratio", Type = MetadataV2FieldType.Double, Nullable = true },
            new MetadataV2Field { Name = "active", Type = MetadataV2FieldType.Boolean, Nullable = true },
            new MetadataV2Field
            {
                Name = "shape",
                Type = MetadataV2FieldType.Geometry,
                Nullable = true,
                SemanticRoles = ["geometry.primary"],
            },
        ],
        Spatial = new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.Point,
            PrimaryGeometryField = "shape",
        },
    };

    var wkbWriter = new WKBWriter();
    var features = new[]
    {
        MakeFeature(1, -122.4194, 37.7749, "Harbor City", 1000000, 0.91, true),
        MakeFeature(2, -122.2711, 37.8044, "Baytown", 430000, 0.42, true),
        MakeFeature(3, 0.0, 51.4779, "Meridian Marker", 0, 0.0, false),
    };

    var (payload, _) = GeoParquetFeatureWriter.FormatAsGeoParquet(
        QueryResult<Feature>.Create(features.Length, [.. features]),
        resource,
        objectIdFieldName: "objectid",
        returnGeometry: true,
        outputSrid: 4326,
        returnZ: false,
        returnM: false,
        new GeometryLimits());

    File.WriteAllBytes(path, payload);
    Console.WriteLine($"GeoParquet: {path} ({payload.Length} bytes, {features.Length} features)");

    Feature MakeFeature(long id, double lon, double lat, string name, int population, double ratio, bool active)
        => Feature.Create(
            id,
            wkbWriter.Write(new Point(lon, lat) { SRID = 4326 }),
            new Dictionary<string, object?>
            {
                ["objectid"] = id,
                ["name"] = name,
                ["population"] = population,
                ["ratio"] = ratio,
                ["active"] = active,
            }.ToImmutableDictionary());
}

// --- PMTiles v3 -----------------------------------------------------------

static async Task GeneratePMTilesAsync(string path)
{
    // honua's MVT path stores already-encoded vector tiles; the PMTiles archive
    // wraps them. `pmtiles verify` validates the v3 header, the root/leaf
    // directory tree, clustered ordering and tile offsets — not the inner MVT
    // geometry — so deterministic non-empty payloads exercise the archive
    // structure the writer produces. Spread tiles across a few z/x/y so the
    // Hilbert-ordered directory has multiple entries to verify.
    var writer = new PMTilesWriter(
        tileCompression: PMTilesCompression.Gzip,
        internalCompression: PMTilesCompression.Gzip);

    writer.AddTile(0, 0, 0, MakeTilePayload(0));
    writer.AddTile(1, 0, 0, MakeTilePayload(1));
    writer.AddTile(1, 1, 1, MakeTilePayload(2));
    writer.AddTile(2, 2, 1, MakeTilePayload(3));
    writer.AddTile(2, 1, 2, MakeTilePayload(4));

    var metadata = new PMTilesArchiveMetadata
    {
        MinLon = -180,
        MinLat = -85.0511,
        MaxLon = 180,
        MaxLat = 85.0511,
        MinZoom = 0,
        MaxZoom = 2,
        Attribution = "Honua CNG conformance lane",
        Description = "Synthetic PMTiles archive emitted by honua PMTilesWriter for pmtiles verify",
    };

    await using var stream = File.Create(path);
    var bytes = await writer.WriteAsync(stream, metadata).ConfigureAwait(false);
    Console.WriteLine($"PMTiles archive: {path} ({bytes} bytes, {writer.TileCount} tiles)");
}

static byte[] MakeTilePayload(int seed)
{
    // A minimal, non-empty payload. Content bytes are opaque to `pmtiles verify`.
    var payload = new byte[16];
    for (var i = 0; i < payload.Length; i++)
    {
        payload[i] = (byte)((seed * 31 + i * 7) & 0xFF);
    }

    return payload;
}

// --- 3D Tiles + glTF ------------------------------------------------------

static void GenerateThreeDTiles(string dir)
{
    Directory.CreateDirectory(dir);

    // Build a small polygon footprint near San Francisco and extrude it into a
    // vertical prism so the GLB carries real triangle geometry that
    // gltf_validator can validate.
    var features = new List<SceneFeature>
    {
        new()
        {
            Id = 1,
            Geometry = new SceneFeatureGeometry
            {
                Kind = SceneGeometryKind.Polygon,
                Vertices =
                [
                    new SceneVertex(-122.4200, 37.7740, 0),
                    new SceneVertex(-122.4180, 37.7740, 0),
                    new SceneVertex(-122.4180, 37.7760, 0),
                    new SceneVertex(-122.4200, 37.7760, 0),
                    new SceneVertex(-122.4200, 37.7740, 0),
                ],
            },
            Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "CNG Building",
                ["height"] = 25.0,
            },
        },
    };

    var attributes = new List<SceneAttributeSchema>
    {
        new() { PropertyId = "name", FieldName = "name", SchemaType = "STRING" },
        new() { PropertyId = "height", FieldName = "height", SchemaType = "SCALAR", SchemaComponentType = "FLOAT32" },
    };

    var extrusion = new Honua.Core.Features.Metadata.Domain.V2.MetadataV2ExtrusionInfo
    {
        HeightField = "height",
        DefaultHeight = 25.0,
    };

    var glb = GeometryTileBuilder.BuildGlb(
        features,
        attributes,
        extrusion,
        generatorTag: "honua-cng-conformance");

    var glbPath = Path.Combine(dir, "content.glb");
    File.WriteAllBytes(glbPath, glb);
    Console.WriteLine($"glTF GLB content: {glbPath} ({glb.Length} bytes)");

    // Bounding region [west, south, east, north] in degrees, plus the extruded
    // vertical extent. geometricError must be positive for the root tile.
    var tileset = TilesetDocumentWriter.Build(
        boundingRegionDegrees: [-122.4200, 37.7740, -122.4180, 37.7760],
        minHeightMeters: 0.0,
        maxHeightMeters: 25.0,
        geometricError: 16.0,
        tileContentUris: ["content.glb"],
        generatorTag: "honua-cng-conformance");

    var tilesetBytes = TilesetDocumentWriter.Serialize(tileset);
    var tilesetPath = Path.Combine(dir, "tileset.json");
    File.WriteAllBytes(tilesetPath, tilesetBytes);
    Console.WriteLine($"3D Tiles tileset: {tilesetPath} ({tilesetBytes.Length} bytes)");
}
