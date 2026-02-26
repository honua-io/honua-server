import Color from "@arcgis/core/Color";
import SimpleRenderer from "@arcgis/core/renderers/SimpleRenderer";
import UniqueValueRenderer from "@arcgis/core/renderers/UniqueValueRenderer";

const baseColor = new Color([255, 102, 0, 0.8]);

const simple = new SimpleRenderer({
  symbol: {
    type: "simple-fill",
    color: baseColor,
  },
  label: "All features",
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
void unique;
