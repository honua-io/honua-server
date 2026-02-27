import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import FeatureForm from "@arcgis/core/widgets/FeatureForm";

const parcels = new FeatureLayer({
  url: "https://example.test/rest/services/default/FeatureServer/0",
});

const form = new FeatureForm({
  layer: parcels,
  container: "feature-form",
  feature: {
    attributes: { OBJECTID: 1, status: "Open" },
  },
  fieldConfig: [{ name: "status" }],
});

void form;
