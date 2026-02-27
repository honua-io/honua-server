import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import LayerList from "@arcgis/core/widgets/LayerList";
import Legend from "@arcgis/core/widgets/Legend";
import Popup from "@arcgis/core/widgets/Popup";
import Search from "@arcgis/core/widgets/Search";
import Home from "@arcgis/core/widgets/Home";
import BasemapToggle from "@arcgis/core/widgets/BasemapToggle";
import Locate from "@arcgis/core/widgets/Locate";
import ScaleBar from "@arcgis/core/widgets/ScaleBar";
import Expand from "@arcgis/core/widgets/Expand";
import Bookmarks from "@arcgis/core/widgets/Bookmarks";
import Fullscreen from "@arcgis/core/widgets/Fullscreen";
import Zoom from "@arcgis/core/widgets/Zoom";
import Attribution from "@arcgis/core/widgets/Attribution";

const parcelsLayer = new FeatureLayer({
  url: "https://example.test/rest/services/parcels/FeatureServer/0",
  outFields: ["*"],
});

const map = new Map({
  basemap: {
    id: "streets",
    baseLayers: [{ id: "base-1" }],
    referenceLayers: [{ id: "ref-1" }],
  },
  layers: [parcelsLayer],
  tables: [{ id: "parcels-table" }],
});

const view = new MapView({
  map,
  container: "viewDiv",
  center: [-157.8583, 21.3069],
  zoom: 12,
});

const layerList = new LayerList({ view });
const legend = new Legend({ view });
const popup = new Popup({ view, dockEnabled: true });
const home = new Home({
  view,
  viewpoint: { center: [-157.8583, 21.3069], zoom: 12 },
});
const basemapToggle = new BasemapToggle({
  view,
  nextBasemap: {
    id: "satellite",
    baseLayers: [{ id: "sat-base" }],
  },
});
const locate = new Locate({
  view,
  zoom: 14,
  locateProvider: async () => ({
    coords: {
      latitude: 21.307,
      longitude: -157.857,
    },
  }),
});
const scaleBar = new ScaleBar({ view, unit: "dual" });
const search = new Search({
  view,
  includeDefaultSources: false,
  autoNavigate: false,
  sources: [
    {
      search: async ({ searchTerm }) => [
        { name: `Parcel ${searchTerm}-A`, location: { x: -157.85, y: 21.30 } },
      ],
    },
    {
      search: async ({ searchTerm }) => [
        { name: `Parcel ${searchTerm}-B`, location: { x: -157.84, y: 21.31 } },
      ],
    },
  ],
});
const expand = new Expand({ view, content: legend, expanded: false });
const bookmarks = new Bookmarks({
  view,
  bookmarks: [{ name: "Downtown", target: { center: [-157.86, 21.30], zoom: 13 } }],
});
const fullscreen = new Fullscreen({ view });
const zoom = new Zoom({ view, layout: "vertical" });
const attribution = new Attribution({ view, attributions: ["Source A", "Source B"] });

view.ui.add([
  layerList,
  legend,
  popup,
  home,
  basemapToggle,
  locate,
  scaleBar,
  search,
  expand,
  bookmarks,
  fullscreen,
  zoom,
  attribution,
], "top-right");

popup.open({
  title: "Parcels",
  content: "Loaded",
  features: [{ id: "parcel-1" }, { id: "parcel-2" }],
  location: [-157.85, 21.30],
});
const popupBefore = popup.selectedFeature;
popup.next();
const popupAfterNext = popup.selectedFeature;

await home.go();
const toggledBasemap = basemapToggle.toggle();
const locateResult = await locate.locate();
const scaleText = scaleBar.refresh();
const searchResponse = await search.search("honua");
await search.nextResult();

export default {
  mapCtor: map.constructor.name,
  viewCtor: view.constructor.name,
  layerCtor: parcelsLayer.constructor.name,
  widgetCtors: [
    layerList.constructor.name,
    legend.constructor.name,
    popup.constructor.name,
    home.constructor.name,
    basemapToggle.constructor.name,
    locate.constructor.name,
    scaleBar.constructor.name,
    search.constructor.name,
    expand.constructor.name,
    bookmarks.constructor.name,
    fullscreen.constructor.name,
    zoom.constructor.name,
    attribution.constructor.name,
  ],
  uiCount: view.ui.getComponents().length,
  popupBefore,
  popupAfterNext,
  toggledBasemapId: toggledBasemap?.id ?? toggledBasemap,
  locateLongitude: locateResult.coords.longitude,
  locateLatitude: locateResult.coords.latitude,
  searchResultCount: searchResponse.results.length,
  searchSelectedResult: search.selectedResult?.name,
  scaleText,
};
