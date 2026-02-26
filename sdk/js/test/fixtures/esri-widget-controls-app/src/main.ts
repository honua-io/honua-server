import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import LayerList from "@arcgis/core/widgets/LayerList";
import Legend from "@arcgis/core/widgets/Legend";
import Popup from "@arcgis/core/widgets/Popup";
import Home from "@arcgis/core/widgets/Home";
import BasemapToggle from "@arcgis/core/widgets/BasemapToggle";
import Locate from "@arcgis/core/widgets/Locate";
import ScaleBar from "@arcgis/core/widgets/ScaleBar";
import Search from "@arcgis/core/widgets/Search";
import BasemapGallery from "@arcgis/core/widgets/BasemapGallery";
import Expand from "@arcgis/core/widgets/Expand";
import Compass from "@arcgis/core/widgets/Compass";

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
const home = new Home({ view });
const basemapToggle = new BasemapToggle({ view, nextBasemap: "satellite" });
const locate = new Locate({ view });
const scaleBar = new ScaleBar({ view, unit: "dual" });
const search = new Search({ view, container: "search-div", includeDefaultSources: false });
const basemapGallery = new BasemapGallery({ view, container: "gallery-div" });
const compass = new Compass({ view });
const expand = new Expand({ view, content: legend, expanded: false });

view.ui.add(layerList, "top-right");
view.ui.add([legend, home], "top-left");
view.ui.add(popup, { position: "manual", index: 0 });
view.ui.add([basemapToggle, locate, scaleBar], "bottom-right");
view.ui.add(search, "top-left");
view.ui.add(basemapGallery, "top-right");
view.ui.add(compass, "top-left");
view.ui.add(expand, "top-right");

popup.open({
  title: "Migration",
  content: "Widget constructors should migrate to compat classes",
});

void map;
void view;
void layerList;
void legend;
void popup;
void home;
void basemapToggle;
void locate;
void scaleBar;
void search;
void basemapGallery;
void compass;
void expand;
