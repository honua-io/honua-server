import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import WebMap from "@arcgis/core/WebMap";

const layerUrl = "https://example.test/rest/services/default/FeatureServer/0";
const simple = new FeatureLayer({ url: "https://example.test/rest/services/default/FeatureServer/0" });
const complex = new FeatureLayer({ url: layerUrl, outFields: ["*"] });
const map = new Map({ basemap: "streets-vector", layers: [simple] });
const mapView = new MapView({
  map,
  container: "viewDiv",
  zoom: 4,
  center: [-157.8, 21.3],
});
const webMap = new WebMap({});

void map;
void mapView;
void webMap;
void simple;
void complex;
