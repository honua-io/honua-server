import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import BasemapLayerList from "@arcgis/core/widgets/BasemapLayerList";

const map = new Map({
  basemap: "streets-vector",
});

const view = new MapView({
  map,
  center: [-157.8583, 21.3069],
  zoom: 12,
});

const basemapLayerList = new BasemapLayerList({
  view,
  container: "basemap-layer-list",
});

void basemapLayerList;
