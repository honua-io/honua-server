import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import WebMap from "@arcgis/core/WebMap";

const layerUrl = "https://example.test/rest/services/default/FeatureServer/0";
const simple = new FeatureLayer({ url: "https://example.test/rest/services/default/FeatureServer/0" });
const complex = new FeatureLayer({ url: layerUrl, outFields: ["*"] });
const mapView = new MapView({});
const webMap = new WebMap({});

void mapView;
void webMap;
void simple;
void complex;
