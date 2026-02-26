import WebMap from "@arcgis/core/WebMap";
import MapView from "@arcgis/core/views/MapView";

const map = new WebMap({
  portalItem: {
    id: "abc123",
  },
});

const view = new MapView({
  map,
  container: "viewDiv",
});

void view;
