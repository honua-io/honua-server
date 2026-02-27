import Map from "@arcgis/core/Map";
import GraphicsLayer from "@arcgis/core/layers/GraphicsLayer";
import GroupLayer from "@arcgis/core/layers/GroupLayer";

const graphics = new GraphicsLayer({
  id: "graphics-layer",
  visible: true,
  opacity: 0.8,
});

const group = new GroupLayer({
  id: "group-layer",
  visibilityMode: "independent",
  layers: [graphics],
});

const map = new Map({
  basemap: "streets",
  layers: [group],
});

void graphics;
void group;
void map;
