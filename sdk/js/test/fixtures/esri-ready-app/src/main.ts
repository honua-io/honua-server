import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";

const layer = new FeatureLayer({
  url: "https://example.test/rest/services/default/FeatureServer/0",
});
const map = new Map({
  basemap: "streets",
  layers: [layer],
});
const mapView = new MapView({
  map,
  center: [-157.8583, 21.3069],
  zoom: 10,
});

void mapView;
