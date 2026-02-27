import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import FeatureTemplates from "@arcgis/core/widgets/FeatureTemplates";

const parcels = new FeatureLayer({
  url: "https://example.test/rest/services/default/FeatureServer/0",
});

const templates = new FeatureTemplates({
  layerInfos: [{ layer: parcels }],
  container: "feature-templates",
  filterFunction: (item) => item.name !== "Restricted",
});

void templates;
