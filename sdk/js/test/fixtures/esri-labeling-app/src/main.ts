import PictureMarkerSymbol from "@arcgis/core/symbols/PictureMarkerSymbol";
import TextSymbol from "@arcgis/core/symbols/TextSymbol";
import LabelClass from "@arcgis/core/layers/support/LabelClass";

const marker = new PictureMarkerSymbol({
  url: "https://example.test/marker.png",
  width: 20,
  height: 20,
  opacity: 0.9,
});

const text = new TextSymbol({
  text: "Parcel",
  color: "#1f2937",
  haloColor: "#ffffff",
  haloSize: 1,
  xoffset: 2,
  yoffset: -2,
});

const labels = new LabelClass({
  labelExpressionInfo: { expression: "$feature.NAME" },
  symbol: text,
  where: "status = 'active'",
  minScale: 0,
  maxScale: 0,
});

void marker;
void labels;
