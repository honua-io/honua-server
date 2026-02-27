import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import Swipe from "@arcgis/core/widgets/Swipe";

const map = new Map({
  basemap: "streets-vector",
});

const view = new MapView({
  map,
  center: [-157.8583, 21.3069],
  zoom: 12,
});

const swipe = new Swipe({
  view,
  container: "swipe-widget",
  position: 45,
  leadingLayers: [],
  trailingLayers: [],
});

view.ui.add(swipe, "top-right");

void swipe;
