import Graphic from "@arcgis/core/Graphic";
import Point from "@arcgis/core/geometry/Point";
import SimpleLineSymbol from "@arcgis/core/symbols/SimpleLineSymbol";
import SimpleMarkerSymbol from "@arcgis/core/symbols/SimpleMarkerSymbol";

const geometry = new Point({
  x: -157.81,
  y: 21.30,
  spatialReference: { wkid: 4326 },
});

const outline = new SimpleLineSymbol({
  style: "solid",
  color: "white",
  width: 1,
});

const symbol = new SimpleMarkerSymbol({
  style: "circle",
  color: "orange",
  size: 12,
  outline,
});

const graphic = new Graphic({
  geometry,
  symbol,
  attributes: { OBJECTID: 1 },
});

void graphic;
