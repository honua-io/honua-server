import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import LayerList from "@arcgis/core/widgets/LayerList";
import PopupTemplate from "@arcgis/core/PopupTemplate";

const map = new Map({
  basemap: "streets",
});

const view = new MapView({
  map,
  container: "viewDiv",
  center: [-157.86, 21.31],
  zoom: 11,
});

const parcels = new FeatureLayer({
  url: "https://example.test/rest/services/parcels/FeatureServer/0",
  outFields: ["OBJECTID", "NAME"],
});
map.layers.push(parcels);

const popupTemplate = new PopupTemplate({
  title: "{NAME}",
  content: "Parcel details",
  outFields: ["OBJECTID", "NAME"],
});

const layerList = new LayerList({
  view,
  container: "layer-list",
  listItemCreatedFunction: (event) => {
    event.item.actionsSections = [[{ id: "zoom-to", title: "Zoom To" }]];
  },
});

layerList.on("trigger-action", (event) => {
  if (event.action.id === "zoom-to") {
    view.goTo({
      target: event.item.layer.fullExtent,
    });
  }
});

void layerList;
void popupTemplate;
