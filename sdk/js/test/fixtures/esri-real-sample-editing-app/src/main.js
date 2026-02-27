import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import Feature from "@arcgis/core/widgets/Feature";
import FeatureForm from "@arcgis/core/widgets/FeatureForm";
import FeatureTemplates from "@arcgis/core/widgets/FeatureTemplates";
import TableList from "@arcgis/core/widgets/TableList";
import Sketch from "@arcgis/core/widgets/Sketch";
import Editor from "@arcgis/core/widgets/Editor";
import Track from "@arcgis/core/widgets/Track";
import Measurement from "@arcgis/core/widgets/Measurement";
import TimeSlider from "@arcgis/core/widgets/TimeSlider";
import Swipe from "@arcgis/core/widgets/Swipe";

const parcelsLayer = new FeatureLayer({
  url: "https://example.test/rest/services/parcels/FeatureServer/0",
  outFields: ["*"],
});

const map = new Map({
  basemap: "topo-vector",
  layers: [parcelsLayer],
  tables: [{ id: "edit-table-1" }, { id: "edit-table-2" }],
});

const view = new MapView({
  map,
  container: "viewDiv",
  center: [-157.8583, 21.3069],
  zoom: 13,
});

const featureWidget = new Feature({
  view,
  title: "Selected Feature",
  graphic: { attributes: { OBJECTID: 101, status: "open" } },
});
const featureForm = new FeatureForm({
  view,
  layer: parcelsLayer,
  feature: { attributes: { OBJECTID: 101, status: "open" } },
  fieldConfig: [{ name: "status" }],
});
const featureTemplates = new FeatureTemplates({
  view,
  layerInfos: [{ layer: parcelsLayer }],
  filterFunction: (item) => item.id !== "restricted",
});
const tableList = new TableList({ view });
const sketch = new Sketch({ view, layer: undefined, creationMode: "update" });
const editor = new Editor({ view, layerInfos: [{ layer: parcelsLayer }], allowedWorkflows: ["create", "update"] });
const track = new Track({
  view,
  goToLocationEnabled: true,
  useHeadingEnabled: true,
  rotationEnabled: true,
  trackProvider: async () => ({
    coords: {
      latitude: 21.307,
      longitude: -157.857,
      heading: 14,
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
  stops: { values: ["2024-01-01T00:00:00.000Z", "2024-02-01T00:00:00.000Z"] },
});
const swipe = new Swipe({
  view,
  leadingLayers: [parcelsLayer],
  trailingLayers: [],
  position: 40,
});

view.ui.add([
  featureWidget,
  featureForm,
  featureTemplates,
  tableList,
  sketch,
  editor,
  track,
  measurement,
  timeSlider,
  swipe,
], "top-right");

featureTemplates.setTemplates([
  { id: "open", name: "Open" },
  { id: "restricted", name: "Restricted" },
]);
const selectedTemplate = featureTemplates.selectTemplate("open");
const formSubmit = await featureForm.submit({ status: "approved" });

sketch.create("point");
const sketchResult = sketch.complete({ id: "graphic-1" });
const createWorkflowStarted = editor.startCreateWorkflowAtFeatureTypeSelection(parcelsLayer);
const updateWorkflowStarted = editor.startUpdateWorkflowAtFeatureSelection();

const tracked = await track.start();
const distanceMeasurement = measurement.measureDistance([
  [-157.8583, 21.3069],
  [-157.8570, 21.3072],
]);

timeSlider.play();
timeSlider.stop();
const nextExtent = timeSlider.next();
swipe.setPosition(65);

export default {
  mapCtor: map.constructor.name,
  viewCtor: view.constructor.name,
  layerCtor: parcelsLayer.constructor.name,
  widgetCtors: [
    featureWidget.constructor.name,
    featureForm.constructor.name,
    featureTemplates.constructor.name,
    tableList.constructor.name,
    sketch.constructor.name,
    editor.constructor.name,
    track.constructor.name,
    measurement.constructor.name,
    timeSlider.constructor.name,
    swipe.constructor.name,
  ],
  uiCount: view.ui.getComponents().length,
  mapTableCount: tableList.useMapTables().length,
  selectedTemplateName: selectedTemplate?.name,
  formStatus: formSubmit.values.status,
  sketchState: sketch.state,
  sketchCompleteState: sketchResult?.state,
  createWorkflowStarted,
  updateWorkflowStarted,
  trackedLongitude: tracked.coords.longitude,
  trackedLatitude: tracked.coords.latitude,
  measuredDistance: distanceMeasurement.value,
  nextExtentEnd: nextExtent?.end,
  swipePosition: swipe.position,
};
