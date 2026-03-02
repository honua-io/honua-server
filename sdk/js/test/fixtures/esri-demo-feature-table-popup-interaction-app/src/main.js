import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import FeatureTable from "@arcgis/core/widgets/FeatureTable";
import Popup from "@arcgis/core/widgets/Popup";

const incidentsLayer = new FeatureLayer({
  url: "https://example.test/rest/services/default/FeatureServer/0",
  outFields: ["OBJECTID", "NAME", "priority"],
});

const tableRows = [
  {
    objectId: 201,
    name: "Incident A",
    priority: "high",
    geometry: { x: -157.8582, y: 21.3067 },
  },
  {
    objectId: 202,
    name: "Incident B",
    priority: "high",
    geometry: { x: -157.8571, y: 21.3074 },
  },
  {
    objectId: 203,
    name: "Incident C",
    priority: "low",
    geometry: { x: -157.8563, y: 21.3081 },
  },
];

incidentsLayer.queryFeatures = async (options = {}) => {
  const where = typeof options.where === "string" ? options.where : "1=1";
  const filteredRows = where.includes("priority = 'high'")
    ? tableRows.filter((row) => row.priority === "high")
    : tableRows;
  return {
    features: filteredRows.map((row) => ({
      attributes: {
        OBJECTID: row.objectId,
        NAME: row.name,
        priority: row.priority,
      },
      geometry: row.geometry,
    })),
  };
};

const map = new Map({
  basemap: "streets",
  layers: [incidentsLayer],
});

const view = new MapView({
  map,
  container: "viewDiv",
  center: [-157.8575, 21.3073],
  zoom: 15,
});

const popup = new Popup({
  view,
  dockEnabled: true,
});

const table = new FeatureTable({
  view,
  layer: incidentsLayer,
  where: "1=1",
  highlightIds: [],
  paginationEnabled: true,
  relatedRecordsEnabled: false,
  filterBySelectionEnabled: false,
  container: "tableDiv",
});

view.ui.add([popup, table], "top-right");

let tableMapSyncOpened = false;
table.highlightIds.on("change", () => {
  const selectedRows = table.getSelectedRows();
  if (selectedRows.length === 0) {
    popup.close();
    return;
  }

  popup.open({
    title: "Selected incidents",
    features: selectedRows.map((row) => ({
      id: `incident-${row.objectId}`,
      attributes: row.attributes,
      geometry: row.geometry,
    })),
    location: selectedRows[0].geometry,
  });
  tableMapSyncOpened = true;
});

await table.when();
const tableSizeBeforeFilter = table.size;

table.selectRows([201, 202]);
popup.next();

table.filterBySelectionEnabled = true;
table.setWhere("priority = 'high'");
await table.refresh();
table.selectRows([202]);

export default {
  mapCtor: map.constructor.name,
  viewCtor: view.constructor.name,
  layerCtor: incidentsLayer.constructor.name,
  tableCtor: table.constructor.name,
  popupCtor: popup.constructor.name,
  tableSizeBeforeFilter,
  tableSizeAfterFilter: table.size,
  selectedObjectIds: table.getSelectedObjectIds(),
  popupSelectedId: popup.selectedFeature?.id,
  popupVisible: popup.visible,
  where: table.where,
  filterBySelectionEnabled: table.filterBySelectionEnabled,
  tableMapSyncOpened,
};
