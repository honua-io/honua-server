import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import TableList from "@arcgis/core/widgets/TableList";

const map = new Map({
  basemap: "streets-vector",
});

const view = new MapView({
  map,
  center: [-157.8583, 21.3069],
  zoom: 12,
});

const tableList = new TableList({
  view,
  container: "table-list-widget",
  tables: [{ id: "parcels" }],
});

void tableList;
