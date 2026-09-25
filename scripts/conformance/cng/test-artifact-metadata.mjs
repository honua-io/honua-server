import assert from "node:assert/strict";
import test from "node:test";
import { flatGeobufMetadata, pmtilesMetadata } from "./artifact-metadata.mjs";

test("FlatGeobuf observations come from decoded values, including changed count and extent", () => {
  const features = [[10, 30], [-4, 7]].map(coordinates => ({ geometry: { type: "Point", coordinates } }));
  const observed = flatGeobufMetadata(features, { crs: { org: "EPSG", code: 3857 } });
  assert.deepEqual(observed, {
    geometry_type: "Point", feature_count: 2, crs: "EPSG:3857", bounds: [-4, 7, 10, 30],
  });
  assert.equal(flatGeobufMetadata(features, {}).crs, undefined);
});

test("mixed geometry and empty/nonfinite data cannot masquerade as the point fixture", () => {
  const features = [
    { geometry: { type: "Point", coordinates: [1, 2] } },
    { geometry: { type: "LineString", coordinates: [[3, 4], [5, 6]] } },
  ];
  assert.equal(flatGeobufMetadata(features, {}).geometry_type, "LineString,Point");
  assert.throws(() => flatGeobufMetadata([], {}), /No decoded geometry bounds/);
  assert.throws(() => flatGeobufMetadata([{ geometry: { type: "Point", coordinates: [NaN, 2] } }], {}), /Non-finite/);
});

test("PMTiles header observations retain actual type/count/bounds and do not claim transfer", () => {
  const observed = pmtilesMetadata({
    specVersion: 3, tileType: 1, minLon: 1, minLat: 2, maxLon: 3, maxLat: 4,
    minZoom: 2, maxZoom: 4, numAddressedTiles: 9,
  });
  assert.deepEqual(observed, {
    spec_version: "3", tile_type: "mvt", bounds: [1, 2, 3, 4], zoom_range: [2, 4], tile_count: 9,
  });
  assert.equal(pmtilesMetadata({ tileType: 2 }).tile_type, "png");
  assert.equal(pmtilesMetadata({ tileType: 88 }).tile_type, "88");
  assert.equal(observed.observed_transfer, undefined);
});
