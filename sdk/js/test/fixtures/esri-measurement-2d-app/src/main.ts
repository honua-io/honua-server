import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import DistanceMeasurement2D from "@arcgis/core/widgets/DistanceMeasurement2D";
import AreaMeasurement2D from "@arcgis/core/widgets/AreaMeasurement2D";

const map = new Map({
  basemap: "streets-vector",
});

const view = new MapView({
  map,
  center: [-157.8583, 21.3069],
  zoom: 12,
});

const distance = new DistanceMeasurement2D({
  view,
  container: "distance-2d",
  unit: "kilometers",
});

const area = new AreaMeasurement2D({
  view,
  container: "area-2d",
  unit: "square-kilometers",
});

void distance;
void area;
