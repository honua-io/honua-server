import { createHash } from "node:crypto";
// Metadata observations from canonical-client decoded values only.
export function flatGeobufMetadata(features, header) {
  const bounds = [Infinity, Infinity, -Infinity, -Infinity];
  function visit(coordinates) {
    if (!Array.isArray(coordinates)) throw new Error("Missing GeoJSON coordinates");
    if (typeof coordinates[0] === "number") {
      const [x, y] = coordinates;
      if (!Number.isFinite(x) || !Number.isFinite(y)) throw new Error("Non-finite GeoJSON position");
      bounds[0] = Math.min(bounds[0], x);
      bounds[1] = Math.min(bounds[1], y);
      bounds[2] = Math.max(bounds[2], x);
      bounds[3] = Math.max(bounds[3], y);
    } else {
      for (const child of coordinates) visit(child);
    }
  }
  function geometry(value) {
    if (value.type === "GeometryCollection") {
      for (const child of value.geometries) geometry(child);
    } else {
      visit(value.coordinates);
    }
  }
  for (const feature of features) geometry(feature.geometry);
  if (!bounds.every(Number.isFinite)) throw new Error("No decoded geometry bounds");
  const types = [...new Set(features.map(feature => feature.geometry.type))].sort();
  const crs = header?.crs;
  const code = crs?.code_string || crs?.code;
  // FlatGeobuf specifies null org = EPSG, while code 0 remains unknown:
  // https://github.com/flatgeobuf/flatgeobuf/blob/64ca86375654912d21b86a18bb5a560ca0aaf388/src/fbs/header.fbs#L54-L60
  const authority = crs?.org ?? "EPSG";
  return {
    geometry_type: types.join(","),
    feature_count: features.length,
    ...(authority && code ? { crs: `${authority}:${code}` } : {}),
    bounds,
  };
}

export function pmtilesMetadata(header) {
  return {
    spec_version: String(header.specVersion),
    tile_type: ({ 0: "unknown", 1: "mvt", 2: "png", 3: "jpeg", 4: "webp", 5: "avif" })[header.tileType]
      ?? String(header.tileType),
    bounds: [header.minLon, header.minLat, header.maxLon, header.maxLat],
    zoom_range: [header.minZoom, header.maxZoom],
    tile_count: header.numAddressedTiles,
  };
}

// Validate the existing canonical PMTiles fixture after the official MVT decoder.
export function validatePmtilesFixtureTile(tile, z, x, y) {
  const name = `cng-${z}-${x}-${y}`;
  const layer = tile?.layers?.[name];
  if (Object.keys(tile?.layers ?? {}).length !== 1 || !layer || layer.version !== 2 || layer.extent !== 4096 || layer.length !== 1) {
    throw new Error("PMTiles MVT fixture layer identity/schema differs");
  }
  const feature = layer.feature(0);
  const point = feature.loadGeometry();
  const attribute = Array.from({ length: 32 }, (_, block) =>
    createHash("sha256").update(`cng-mvt:${z}:${x}:${y}:${block}`).digest("hex")).join("");
  if (feature.id !== 1 || feature.type !== 1 || feature.properties.name !== name ||
      feature.properties.fixture !== attribute || point.length !== 1 || point[0].length !== 1 ||
      point[0][0].x !== 100 + x || point[0][0].y !== 200 + y) {
    throw new Error("PMTiles MVT fixture feature/point/attribute differs");
  }
}
