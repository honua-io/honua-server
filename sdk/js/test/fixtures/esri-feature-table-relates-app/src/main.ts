import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import FeatureTable from "@arcgis/core/widgets/FeatureTable";

const layer = new FeatureLayer({
  url: "https://example.test/rest/services/default/FeatureServer/0",
  outFields: ["OBJECTID", "FACILITYID"],
});

const map = new Map({
  basemap: "streets-vector",
  layers: [layer],
});

const view = new MapView({
  map,
  container: "viewDiv",
  center: [-157.86, 21.31],
  zoom: 16,
  popup: {
    dockEnabled: true,
    dockOptions: { breakpoint: false },
  },
});

const table = new FeatureTable({
  title: () => `Rows: ${table?.size ?? 0}`,
  description: "Hydrants are related to inspections",
  actionColumnConfig: {
    label: "Go to feature",
    icon: "zoom-to-object",
    callback: (params) => {
      view.goTo(params.feature);
    },
  },
  view,
  attachmentsEnabled: true,
  paginationEnabled: true,
  editingEnabled: true,
  relatedRecordsEnabled: true,
  layer,
  tableTemplate: {
    columnTemplates: [
      {
        type: "field",
        fieldName: "FACILITYID",
        label: "Facility ID",
        autoWidth: true,
      },
    ],
  },
  where: "1=1",
  filterGeometry: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 },
  filterBySelectionEnabled: false,
  container: "tableDiv",
});

table.highlightIds.push(1);
const index = table.highlightIds.indexOf(1);
if (index > -1) {
  table.highlightIds.splice(index, 1);
}
table.highlightIds.on("change", (event) => {
  void event.added;
  void event.removed;
});

void table;
