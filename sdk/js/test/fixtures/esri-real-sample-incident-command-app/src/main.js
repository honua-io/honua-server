import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import MapImageLayer from "@arcgis/core/layers/MapImageLayer";
import TileLayer from "@arcgis/core/layers/TileLayer";
import RouteLayer from "@arcgis/core/layers/RouteLayer";
import RouteTask from "@arcgis/core/rest/route/RouteTask";
import Query from "@arcgis/core/rest/support/Query";
import LayerList from "@arcgis/core/widgets/LayerList";
import Legend from "@arcgis/core/widgets/Legend";
import Popup from "@arcgis/core/widgets/Popup";
import Search from "@arcgis/core/widgets/Search";
import Expand from "@arcgis/core/widgets/Expand";
import Bookmarks from "@arcgis/core/widgets/Bookmarks";
import BasemapGallery from "@arcgis/core/widgets/BasemapGallery";
import BasemapLayerList from "@arcgis/core/widgets/BasemapLayerList";
import FeatureForm from "@arcgis/core/widgets/FeatureForm";
import FeatureTemplates from "@arcgis/core/widgets/FeatureTemplates";
import FeatureTable from "@arcgis/core/widgets/FeatureTable";
import Sketch from "@arcgis/core/widgets/Sketch";
import Editor from "@arcgis/core/widgets/Editor";
import Track from "@arcgis/core/widgets/Track";
import Measurement from "@arcgis/core/widgets/Measurement";
import TimeSlider from "@arcgis/core/widgets/TimeSlider";
import Directions from "@arcgis/core/widgets/Directions";
import CoordinateConversion from "@arcgis/core/widgets/CoordinateConversion";
import Print from "@arcgis/core/widgets/Print";

const incidentsLayer = new FeatureLayer({
  id: "incidents-layer",
  url: "https://example.test/rest/services/incidents/FeatureServer/0",
  outFields: ["*"],
});
const zoningLayer = new FeatureLayer({
  id: "zoning-layer",
  url: "https://example.test/rest/services/zoning/FeatureServer/0",
  outFields: ["*"],
});
const operationsLayer = new MapImageLayer({
  id: "operations-layer",
  url: "https://example.test/rest/services/operations/MapServer",
  opacity: 0.75,
  sublayers: [
    { id: 0, title: "Operations" },
    { id: 2, title: "Road Closures" },
  ],
});
const tileLayer = new TileLayer({
  id: "basemap-tiles",
  url: "https://example.test/rest/services/basemap/MapServer",
  opacity: 0.9,
});
const routeLayer = new RouteLayer({
  id: "response-route",
  stops: [
    { name: "Command", location: [-157.8583, 21.3069] },
    { name: "Incident", location: [-157.8495, 21.3120] },
  ],
});

const map = new Map({
  basemap: {
    id: "streets",
    title: "Streets",
    baseLayers: [{ id: "streets-base" }],
    referenceLayers: [{ id: "streets-ref" }],
  },
  layers: [tileLayer, operationsLayer, incidentsLayer, zoningLayer, routeLayer],
  tables: [{ id: "incidents-table" }, { id: "operations-table" }],
});

const view = new MapView({
  map,
  container: "viewDiv",
  center: [-157.855, 21.309],
  zoom: 13,
});

const layerList = new LayerList({ view });
const legend = new Legend({ view });
const popup = new Popup({ view, dockEnabled: true });
const search = new Search({
  view,
  includeDefaultSources: false,
  autoNavigate: false,
  sources: [
    {
      search: async ({ searchTerm }) => [
        { name: `Incident ${searchTerm}-A`, location: { x: -157.851, y: 21.311 } },
      ],
    },
    {
      search: async ({ searchTerm }) => [
        { name: `Incident ${searchTerm}-B`, location: { x: -157.850, y: 21.312 } },
      ],
    },
  ],
});
const bookmarks = new Bookmarks({
  view,
  bookmarks: [
    { name: "Downtown", target: { center: [-157.858, 21.307], zoom: 14 } },
    { name: "Harbor", target: { center: [-157.875, 21.296], zoom: 13 } },
  ],
});
const featureForm = new FeatureForm({
  view,
  layer: incidentsLayer,
  feature: { attributes: { OBJECTID: 91, status: "open" } },
  fieldConfig: [{ name: "status" }],
});
const featureTemplates = new FeatureTemplates({
  view,
  layerInfos: [{ layer: incidentsLayer }],
  filterFunction: (item) => item.id !== "retired",
});
const featureTable = new FeatureTable({
  view,
  layer: incidentsLayer,
  where: "status = 'open'",
  relatedRecordsEnabled: true,
});
const sketch = new Sketch({ view, creationMode: "update" });
const editor = new Editor({
  view,
  layerInfos: [{ layer: incidentsLayer }, { layer: zoningLayer }],
  allowedWorkflows: ["create", "update"],
});
const track = new Track({
  view,
  trackProvider: async () => ({
    coords: {
      latitude: 21.3075,
      longitude: -157.8565,
      heading: 20,
    },
  }),
});
const measurement = new Measurement({
  view,
  activeTool: "distance",
  linearUnit: "kilometers",
  areaUnit: "square-kilometers",
});
const timeSlider = new TimeSlider({
  view,
  mode: "instant",
  stops: {
    values: [
      "2024-01-01T00:00:00.000Z",
      "2024-01-15T00:00:00.000Z",
      "2024-02-01T00:00:00.000Z",
    ],
  },
});
const directions = new Directions({ view, layer: routeLayer, useDefaultRouteLayer: false });
const coordinateConversion = new CoordinateConversion({
  view,
  mode: "capture",
  multipleConversionsEnabled: true,
  formats: ["lonlat", "dms"],
});
const printer = new Print({
  view,
  printServiceUrl: "https://example.test/print",
  templateOptions: { format: "pdf", layout: "a4-landscape" },
});
const basemapGallery = new BasemapGallery({
  view,
  source: [
    {
      id: "streets",
      title: "Streets",
      baseLayers: [{ id: "streets-base" }],
      referenceLayers: [{ id: "streets-ref" }],
    },
    {
      id: "dark-gray",
      title: "Dark Gray",
      baseLayers: [{ id: "dark-base" }],
      referenceLayers: [{ id: "dark-ref" }],
    },
  ],
});
const basemapLayerList = new BasemapLayerList({ view });
const toolsPanel = new Expand({ view, content: [layerList, legend], expanded: true });

view.ui.add([
  layerList,
  legend,
  popup,
  search,
  toolsPanel,
  bookmarks,
  featureForm,
  featureTemplates,
  featureTable,
  sketch,
  editor,
  track,
  measurement,
  timeSlider,
  directions,
  coordinateConversion,
  printer,
  basemapGallery,
  basemapLayerList,
], "top-right");

let layerListActionTriggered = false;
layerList.on("trigger-action", () => {
  layerListActionTriggered = true;
});
layerList.refresh();
layerList.setItemActions(incidentsLayer, [[{ id: "toggle-incident", title: "Toggle Incident Layer" }]]);
layerList.triggerAction("toggle-incident", incidentsLayer);

const foundLayer = map.findLayerById("incidents-layer");

popup.open({
  title: "Incident Details",
  content: "Incident selected",
  features: [{ id: "incident-1" }, { id: "incident-2" }],
  location: [-157.851, 21.311],
});
popup.next();

featureTemplates.setTemplates([
  { id: "open", name: "Open Incident" },
  { id: "retired", name: "Retired" },
]);
const selectedTemplate = featureTemplates.selectTemplate("open");
const formResult = await featureForm.submit({ status: "active-response" });

featureTable.highlightIds.add(11, 12, 13);
featureTable.highlightIds.remove(12);

sketch.create("point");
const sketchComplete = sketch.complete({ id: "incident-sketch" });
const createWorkflowStarted = editor.startCreateWorkflowAtFeatureTypeSelection(incidentsLayer);
const updateWorkflowStarted = editor.startUpdateWorkflowAtFeatureSelection();

const trackedLocation = await track.start();
const distanceMeasurement = measurement.measureDistance([
  [-157.8583, 21.3069],
  [-157.8510, 21.3110],
]);

timeSlider.play();
timeSlider.stop();
const nextExtent = timeSlider.next();

const routeTask = new RouteTask({
  url: "https://example.test/rest/services/network/RouteServer",
});
const routeTaskResult = await routeTask.solve({
  stops: [
    { location: [-157.8583, 21.3069] },
    { location: [-157.8495, 21.3120] },
  ],
});

const solvedDirections = await directions.solve();
const directionSummary = directions.getSummary();
const conversions = coordinateConversion.setLocation([-157.8525, 21.3105]);
const printResult = await printer.execute({ title: "Incident Command Board" });

const query = new Query({
  where: "priority = 'high'",
  outFields: ["OBJECTID", "status", "priority"],
});

const activeBasemap = basemapGallery.select("dark-gray");
basemapLayerList.refresh();
const allSublayers = operationsLayer.allSublayers;
const foundSublayer = operationsLayer.findSublayerById(2);

const searchResults = await search.search("IC");
await search.nextResult();

export default {
  mapCtor: map.constructor.name,
  viewCtor: view.constructor.name,
  layerCtors: [
    incidentsLayer.constructor.name,
    zoningLayer.constructor.name,
    operationsLayer.constructor.name,
    tileLayer.constructor.name,
    routeLayer.constructor.name,
  ],
  widgetCtors: [
    layerList.constructor.name,
    legend.constructor.name,
    popup.constructor.name,
    search.constructor.name,
    toolsPanel.constructor.name,
    bookmarks.constructor.name,
    featureForm.constructor.name,
    featureTemplates.constructor.name,
    featureTable.constructor.name,
    sketch.constructor.name,
    editor.constructor.name,
    track.constructor.name,
    measurement.constructor.name,
    timeSlider.constructor.name,
    directions.constructor.name,
    coordinateConversion.constructor.name,
    printer.constructor.name,
    basemapGallery.constructor.name,
    basemapLayerList.constructor.name,
  ],
  uiCount: view.ui.getComponents().length,
  layerListCount: layerList.items.length,
  layerListActionTriggered,
  foundLayerId: foundLayer?.id,
  popupSelectedId: popup.selectedFeature?.id,
  selectedTemplateName: selectedTemplate?.name,
  formStatus: formResult.values.status,
  highlightCount: featureTable.highlightIds.length,
  sketchState: sketch.state,
  sketchCompletionState: sketchComplete?.state,
  createWorkflowStarted,
  updateWorkflowStarted,
  trackedLatitude: trackedLocation.coords.latitude,
  trackedLongitude: trackedLocation.coords.longitude,
  measuredDistanceMeters: distanceMeasurement.value,
  nextExtentEnd: nextExtent?.end,
  routeTaskCount: routeTaskResult.routeResults.length,
  routeTaskDistance: routeTaskResult.routeResults[0]?.route.attributes.Total_Length,
  directionsPathPoints: solvedDirections?.path?.length ?? 0,
  directionsStopCount: directionSummary?.stopCount ?? 0,
  conversions: conversions.map((value) => value.format),
  primaryConversionText: conversions[0]?.text,
  printUrl: printResult.url,
  queryWhere: query.where,
  activeBasemapId: activeBasemap?.id,
  basemapBaseLayerCount: basemapLayerList.baseLayers.length,
  sublayerCount: allSublayers.length,
  foundSublayerId: foundSublayer?.id,
  searchResultCount: searchResults.results.length,
  searchSelectedResult: search.selectedResult?.name,
};
