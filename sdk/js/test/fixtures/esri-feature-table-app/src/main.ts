import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import FeatureTable from "@arcgis/core/widgets/FeatureTable";

const parcels = new FeatureLayer({
  url: "https://example.test/rest/services/default/FeatureServer/0",
});

const table = new FeatureTable({
  layer: parcels,
  container: "feature-table",
  where: "1=1",
  objectIdField: "OBJECTID",
});

void table;
