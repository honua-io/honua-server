import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import MapImageLayer from "@arcgis/core/layers/MapImageLayer";
import TileLayer from "@arcgis/core/layers/TileLayer";
import RouteLayer from "@arcgis/core/layers/RouteLayer";
import RouteTask from "@arcgis/core/rest/route/RouteTask";
import Directions from "@arcgis/core/widgets/Directions";
import CoordinateConversion from "@arcgis/core/widgets/CoordinateConversion";
import Print from "@arcgis/core/widgets/Print";
import Query from "@arcgis/core/rest/support/Query";

const mapImage = new MapImageLayer({
  url: "https://example.test/rest/services/parcels/MapServer",
  opacity: 0.7,
});
const tileLayer = new TileLayer({
  url: "https://example.test/rest/services/basemap/MapServer",
  opacity: 0.9,
});
const routeLayer = new RouteLayer({
  stops: [
    { name: "Start", location: [-157.8583, 21.3069] },
    { name: "End", location: [-157.8500, 21.3100] },
  ],
});

const map = new Map({
  basemap: "gray-vector",
  layers: [tileLayer, mapImage, routeLayer],
});
const view = new MapView({
  map,
  container: "viewDiv",
  center: [-157.855, 21.308],
  zoom: 12,
});

const routeTask = new RouteTask({
  url: "https://example.test/rest/services/network/RouteServer",
});
const directions = new Directions({
  view,
  layer: routeLayer,
  useDefaultRouteLayer: false,
  showSaveAsButton: false,
});
const coordinateConversion = new CoordinateConversion({
  view,
  mode: "live",
  multipleConversionsEnabled: true,
  formats: ["lonlat", "dms"],
});
const printer = new Print({
  view,
  printServiceUrl: "https://example.test/print",
  templateOptions: { format: "pdf", layout: "a4-landscape" },
});
const query = new Query({
  where: "status = 'active'",
  outFields: ["OBJECTID", "NAME"],
  returnGeometry: true,
});

view.ui.add([directions, coordinateConversion, printer], "top-right");

const routeTaskResult = await routeTask.solve({
  stops: [
    { location: [-157.8583, 21.3069] },
    { location: [-157.8500, 21.3100] },
  ],
});

const routeLayerResult = await routeLayer.solve();
const directionsResult = await directions.solve();
const directionsSummary = directions.getSummary();
const conversions = coordinateConversion.setLocation([-157.855, 21.308]);
const printResult = await printer.execute({ title: "Network Demo" });

export default {
  mapCtor: map.constructor.name,
  viewCtor: view.constructor.name,
  layerCtors: [mapImage.constructor.name, tileLayer.constructor.name, routeLayer.constructor.name],
  widgetCtors: [directions.constructor.name, coordinateConversion.constructor.name, printer.constructor.name],
  uiCount: view.ui.getComponents().length,
  routeTaskCtor: routeTask.constructor.name,
  queryCtor: query.constructor.name,
  routeTaskCount: routeTaskResult.routeResults.length,
  routeLayerPathPoints: routeLayerResult?.path?.length ?? 0,
  directionsPathPoints: directionsResult?.path?.length ?? 0,
  directionsStopCount: directionsSummary?.stopCount ?? 0,
  directionsDistanceMeters: directionsSummary?.distanceMeters ?? 0,
  coordinateFormats: conversions.map((value) => value.format),
  coordinateText: conversions[0]?.text,
  printUrl: printResult.url,
  queryWhere: query.where,
};
