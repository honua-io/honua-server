import Graphic from "@arcgis/core/Graphic";

const parcelGraphic = new Graphic({
  geometry: { type: "point", x: -157.81, y: 21.30 },
  symbol: { type: "simple-marker", color: "orange" },
  attributes: { OBJECTID: 101, status: "active" },
  popupTemplate: { title: "{status}" },
});

void parcelGraphic;
