import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import FeatureTable from "@arcgis/core/widgets/FeatureTable";
import Popup from "@arcgis/core/widgets/Popup";
import LayerList from "@arcgis/core/widgets/LayerList";
import Legend from "@arcgis/core/widgets/Legend";

const hydrantsLayer = new FeatureLayer({
  id: "hydrants",
  title: "Hydrants",
  url: "https://example.test/rest/services/default/FeatureServer/0",
  outFields: ["OBJECTID", "FACILITYID", "status"],
});

const inspectionsLayer = new FeatureLayer({
  id: "inspections",
  title: "Inspections",
  url: "https://example.test/rest/services/default/FeatureServer/1",
  outFields: ["OBJECTID", "FACILITYID", "result"],
});

const tableRows = [
  {
    objectId: 101,
    facilityId: "HYD-101",
    status: "open",
    geometry: { x: -157.8575, y: 21.3072 },
  },
  {
    objectId: 102,
    facilityId: "HYD-102",
    status: "closed",
    geometry: { x: -157.8567, y: 21.3078 },
  },
  {
    objectId: 103,
    facilityId: "HYD-103",
    status: "open",
    geometry: { x: -157.8558, y: 21.3084 },
  },
];

const relatedByObjectId = {
  101: [
    { attributes: { OBJECTID: 9001, FACILITYID: "HYD-101", result: "pass" } },
    { attributes: { OBJECTID: 9002, FACILITYID: "HYD-101", result: "pass" } },
  ],
  102: [{ attributes: { OBJECTID: 9003, FACILITYID: "HYD-102", result: "fail" } }],
  103: [{ attributes: { OBJECTID: 9004, FACILITYID: "HYD-103", result: "pass" } }],
};

hydrantsLayer.queryFeatures = async (options = {}) => {
  const where = typeof options.where === "string" ? options.where : "1=1";
  const filteredRows = where.includes("status = 'open'")
    ? tableRows.filter((row) => row.status === "open")
    : tableRows;
  return {
    features: filteredRows.map((row) => ({
      attributes: {
        OBJECTID: row.objectId,
        FACILITYID: row.facilityId,
        status: row.status,
      },
      geometry: row.geometry,
    })),
  };
};

hydrantsLayer.queryRelatedFeatures = async (options = {}) => {
  const objectIds = Array.isArray(options.objectIds)
    ? options.objectIds
    : typeof options.objectIds === "string"
      ? options.objectIds.split(",").map((value) => Number.parseInt(value, 10))
      : [];
  return {
    relatedRecordGroups: objectIds.map((objectId) => ({
      objectId,
      relatedRecords: relatedByObjectId[String(objectId)] ?? [],
    })),
  };
};

hydrantsLayer.getLegend = async () => ({
  layers: [
    {
      layerId: 0,
      layerName: "Hydrants",
      legend: [
        {
          label: "Open",
          imageData: "open-symbol",
          contentType: "image/png",
          width: 20,
          height: 20,
        },
      ],
    },
  ],
});

inspectionsLayer.getLegend = async () => ({
  layers: [
    {
      layerId: 1,
      layerName: "Inspections",
      legend: [
        {
          label: "Inspection",
          imageData: "inspection-symbol",
          contentType: "image/png",
          width: 20,
          height: 20,
        },
      ],
    },
  ],
});

const map = new Map({
  basemap: "streets-vector",
  layers: [hydrantsLayer, inspectionsLayer],
});

const view = new MapView({
  map,
  container: "viewDiv",
  center: [-157.857, 21.3075],
  zoom: 16,
  popup: {
    dockEnabled: true,
    dockOptions: { breakpoint: false },
  },
});

const popup = new Popup({
  view,
  dockEnabled: true,
  dockOptions: { breakpoint: false },
});

const layerList = new LayerList({
  view,
  listItemCreatedFunction: (event) => {
    event.item.actionsSections = [[{ id: "zoom-to-selection", title: "Zoom to selection" }]];
  },
});

const legend = new Legend({ view });

const table = new FeatureTable({
  title: "Hydrant inspections",
  description: "Hydrants related to inspection records",
  actionColumnConfig: {
    label: "Go to feature",
    icon: "zoom-to-object",
    callback: () => undefined,
  },
  view,
  attachmentsEnabled: true,
  paginationEnabled: true,
  editingEnabled: true,
  relatedRecordsEnabled: true,
  layer: hydrantsLayer,
  tableTemplate: {
    columnTemplates: [
      {
        type: "field",
        fieldName: "FACILITYID",
        label: "Facility ID",
      },
    ],
  },
  where: "1=1",
  filterGeometry: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 },
  filterBySelectionEnabled: false,
  selectionMode: "multiple",
  rowSelectionEnabled: true,
  highlightEnabled: true,
  pageSize: 25,
  autoRefreshEnabled: true,
  container: "tableDiv",
});

view.ui.add([layerList, legend, popup, table], "top-right");

let layerActionTriggered = false;
layerList.on("trigger-action", (event) => {
  if (event.action.id !== "zoom-to-selection") {
    return;
  }
  layerActionTriggered = true;
  const selectedRow = table.getSelectedRows()[0];
  if (selectedRow?.geometry) {
    void view.goTo({
      target: selectedRow.geometry,
      zoom: 17,
    });
  }
});

layerList.refresh();
layerList.setItemActions(hydrantsLayer, [[{ id: "zoom-to-selection", title: "Zoom to selection" }]]);

let tableMapSyncOpened = false;
table.highlightIds.on("change", () => {
  const selectedRows = table.getSelectedRows();
  if (selectedRows.length === 0) {
    popup.close();
    return;
  }

  popup.open({
    title: "Selected hydrants",
    features: selectedRows.map((row) => ({
      id: `hydrant-${row.objectId}`,
      attributes: row.attributes,
      geometry: row.geometry,
    })),
    location: selectedRows[0].geometry,
  });
  tableMapSyncOpened = true;
});

await table.when();
const tableSizeBeforeFilter = table.size;

table.selectRows([101, 102]);
popup.next();
const popupAfterNext = popup.selectedFeature;

table.clearSelection();
table.selectRows([101]);

table.filterBySelectionEnabled = true;
table.setWhere("status = 'open'");
table.setFilterGeometry({
  xmin: -157.859,
  ymin: 21.306,
  xmax: -157.855,
  ymax: 21.309,
});

await table.refresh();
layerList.triggerAction("zoom-to-selection", hydrantsLayer);

const selectedRows = table.getSelectedRows();
const related = await table.queryRelatedRecords({
  relationshipId: 0,
  objectIds: table.getSelectedObjectIds(),
  returnGeometry: false,
});

const relatedRecordCount = related.relatedRecordGroups.reduce(
  (sum, group) => sum + (Array.isArray(group.relatedRecords) ? group.relatedRecords.length : 0),
  0,
);

const legendItems = await legend.refresh();
const legendEntryCount = legendItems.reduce((sum, group) => sum + group.entries.length, 0);

export default {
  mapCtor: map.constructor.name,
  viewCtor: view.constructor.name,
  layerCtors: [hydrantsLayer.constructor.name, inspectionsLayer.constructor.name],
  widgetCtors: [
    table.constructor.name,
    popup.constructor.name,
    layerList.constructor.name,
    legend.constructor.name,
  ],
  tableSizeBeforeFilter,
  tableSizeAfterFilter: table.size,
  selectedObjectIds: table.getSelectedObjectIds(),
  selectedRowCount: selectedRows.length,
  popupSelectedId: popup.selectedFeature?.id,
  popupAfterNextId: popupAfterNext?.id,
  filterBySelectionEnabled: table.filterBySelectionEnabled,
  filterGeometryApplied: table.filterGeometry !== null,
  tableMapSyncOpened,
  relatedGroupCount: related.relatedRecordGroups.length,
  relatedRecordCount,
  layerListCount: layerList.items.length,
  layerActionTriggered,
  legendLayerCount: legendItems.length,
  legendEntryCount,
};
