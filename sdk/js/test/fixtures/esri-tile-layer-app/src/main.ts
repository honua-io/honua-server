import Map from "@arcgis/core/Map";
import TileLayer from "@arcgis/core/layers/TileLayer";

const tiled = new TileLayer({
  url: "https://example.test/rest/services/basemap/MapServer",
  opacity: 0.7,
});

const map = new Map({
  basemap: "gray-vector",
  layers: [tiled],
});

void map;
