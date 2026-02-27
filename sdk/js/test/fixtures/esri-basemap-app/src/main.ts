import Basemap from "@arcgis/core/Basemap";
import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";

const basemap = new Basemap({
  id: "streets-vector",
});

const map = new Map({
  basemap,
});

const view = new MapView({
  container: "viewDiv",
  map,
  center: [-157.8583, 21.3069],
  zoom: 11,
});

void view;
