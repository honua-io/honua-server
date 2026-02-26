import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import LayerList from "@arcgis/core/widgets/LayerList";
import Legend from "@arcgis/core/widgets/Legend";
import Popup from "@arcgis/core/widgets/Popup";

const map = new Map({
  basemap: "streets",
});

const view = new MapView({
  map,
  container: "viewDiv",
});

const layerList = new LayerList({ view });
const legend = new Legend({ view });
const popup = new Popup({ view, dockEnabled: true });

popup.open({
  title: "Migration",
  content: "Widget constructors should migrate to compat classes",
});

void map;
void view;
void layerList;
void legend;
void popup;
