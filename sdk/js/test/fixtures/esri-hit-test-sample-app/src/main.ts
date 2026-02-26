import Map from "@arcgis/core/Map";
import MapView from "@arcgis/core/views/MapView";
import FeatureLayer from "@arcgis/core/layers/FeatureLayer";

const trails = new FeatureLayer({
  url: "https://example.test/rest/services/trails/FeatureServer/0",
});

const map = new Map({
  basemap: "topo-vector",
  layers: [trails],
});

const view = new MapView({
  map,
  container: "viewDiv",
  center: [-118.805, 34.027],
  zoom: 13,
});

view.on("click", async (event) => {
  const hit = await view.hitTest(event);
  const first = hit.results[0];
  if (!first) {
    return;
  }

  view.popup.open({
    title: "Trail",
    features: [first.graphic],
    location: event.mapPoint,
  });
});
