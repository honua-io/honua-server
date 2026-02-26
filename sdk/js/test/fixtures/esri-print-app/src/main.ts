import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import Print from "@arcgis/core/widgets/Print";

const map = new Map({
  basemap: "streets-vector",
});

const view = new MapView({
  map,
  center: [-157.8583, 21.3069],
  zoom: 12,
});

const printer = new Print({
  view,
  container: "print-widget",
  printServiceUrl: "https://example.test/print",
  templateOptions: {
    format: "pdf",
    layout: "a4-landscape",
  },
});

const printResult = await printer.execute({
  title: "Downtown",
  dpi: 150,
});

void printResult;
