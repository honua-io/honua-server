import Color from "@arcgis/core/Color";
import SimpleFillSymbol from "@arcgis/core/symbols/SimpleFillSymbol";
import ClassBreaksRenderer from "@arcgis/core/renderers/ClassBreaksRenderer";
import SimpleRenderer from "@arcgis/core/renderers/SimpleRenderer";
import UniqueValueRenderer from "@arcgis/core/renderers/UniqueValueRenderer";

const baseColor = new Color([255, 102, 0, 0.8]);
const fill = new SimpleFillSymbol({
  style: "solid",
  color: baseColor,
  outline: { color: "white", width: 1 },
});

const simple = new SimpleRenderer({
  symbol: fill,
  label: "All features",
});

const classBreaks = new ClassBreaksRenderer({
  field: "population",
  minValue: 0,
  classBreakInfos: [
    {
      minValue: 0,
      maxValue: 1000,
      label: "0-1000",
      symbol: fill,
    },
  ],
});

const unique = new UniqueValueRenderer({
  field: "status",
  defaultLabel: "Other",
  uniqueValueInfos: [
    {
      value: "open",
      label: "Open",
      symbol: { type: "simple-fill", color: "green" },
    },
  ],
});

void simple;
void classBreaks;
void unique;
