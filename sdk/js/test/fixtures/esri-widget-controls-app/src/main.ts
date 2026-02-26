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
import Bookmarks from "@arcgis/core/widgets/Bookmarks";
import Fullscreen from "@arcgis/core/widgets/Fullscreen";
import Zoom from "@arcgis/core/widgets/Zoom";
import Attribution from "@arcgis/core/widgets/Attribution";
import Sketch from "@arcgis/core/widgets/Sketch";
import Editor from "@arcgis/core/widgets/Editor";
import Track from "@arcgis/core/widgets/Track";
import Measurement from "@arcgis/core/widgets/Measurement";
import TimeSlider from "@arcgis/core/widgets/TimeSlider";
import RouteLayer from "@arcgis/core/layers/RouteLayer";
import Directions from "@arcgis/core/widgets/Directions";
import CoordinateConversion from "@arcgis/core/widgets/CoordinateConversion";

const map = new Map({
  basemap: "streets",
});
const routeLayer = new RouteLayer({
  stops: [
    { name: "Start", location: [-157.0, 21.3] },
    { name: "End", location: [-157.01, 21.31] },
  ],
});
map.add(routeLayer);

const view = new MapView({
  map,
  container: "viewDiv",
});

const layerList = new LayerList({ view });
const legend = new Legend({ view });
const popup = new Popup({ view, dockEnabled: true });
const home = new Home({
  view,
  viewpoint: { center: [0, 0], zoom: 3 },
});
const basemapToggle = new BasemapToggle({ view, nextBasemap: "satellite" });
const locate = new Locate({ view, zoom: 12 });
const scaleBar = new ScaleBar({ view, unit: "dual" });
const search = new Search({ view, container: "search-div", includeDefaultSources: false });
const basemapGallery = new BasemapGallery({ view, container: "gallery-div" });
const compass = new Compass({ view });
const expand = new Expand({ view, content: legend, expanded: false });
const bookmarks = new Bookmarks({
  view,
  bookmarks: [
    {
      name: "Home",
      target: { center: [0, 0], zoom: 2 },
    },
  ],
});
const fullscreen = new Fullscreen({ view });
const zoom = new Zoom({ view, layout: "vertical" });
const attribution = new Attribution({
  view,
  itemDelimiter: " | ",
  attributions: ["Source A"],
});
const sketch = new Sketch({ view, layer: undefined, creationMode: "update" });
const editor = new Editor({ view, layerInfos: [], allowedWorkflows: ["create", "update"] });
const track = new Track({
  view,
  goToLocationEnabled: true,
  useHeadingEnabled: true,
  rotationEnabled: true,
  trackProvider: async () => ({
    coords: {
      latitude: 21.3069,
      longitude: -157.8583,
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
    values: ["2024-01-01T00:00:00.000Z", "2024-02-01T00:00:00.000Z"],
  },
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

view.ui.add(layerList, "top-right");
view.ui.add([legend, home], "top-left");
view.ui.add(popup, { position: "manual", index: 0 });
view.ui.add([basemapToggle, locate, scaleBar], "bottom-right");
view.ui.add(search, "top-left");
view.ui.add(basemapGallery, "top-right");
view.ui.add(compass, "top-left");
view.ui.add(expand, "top-right");
view.ui.add(bookmarks, "top-right");
view.ui.add(fullscreen, "top-left");
view.ui.add(zoom, "top-left");
view.ui.add(attribution, "bottom-left");
view.ui.add(sketch, "top-right");
view.ui.add(editor, "top-right");
view.ui.add(track, "top-left");
view.ui.add(measurement, "bottom-right");
view.ui.add(timeSlider, "bottom-left");
view.ui.add(directions, "top-right");
view.ui.add(coordinateConversion, "bottom-left");

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
void bookmarks;
void fullscreen;
void zoom;
void attribution;
void sketch;
void editor;
void track;
void measurement;
void timeSlider;
void routeLayer;
void directions;
void coordinateConversion;
