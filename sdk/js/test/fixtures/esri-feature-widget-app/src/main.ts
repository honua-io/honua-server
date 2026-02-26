import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import Feature from "@arcgis/core/widgets/Feature";

const map = new Map({
  basemap: "streets-vector",
});

const view = new MapView({
  map,
  center: [-157.8583, 21.3069],
  zoom: 12,
});

const featureWidget = new Feature({
  view,
  container: "feature-widget",
  title: "Selected Parcel",
  graphic: {
    attributes: { OBJECTID: 1, name: "Parcel A" },
    geometry: { type: "point", x: -157.8583, y: 21.3069 },
  },
});

void featureWidget;
