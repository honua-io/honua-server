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
  return {
    geometry_type: types.join(","),
    feature_count: features.length,
    ...(crs?.org && code ? { crs: `${crs.org}:${code}` } : {}),
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
