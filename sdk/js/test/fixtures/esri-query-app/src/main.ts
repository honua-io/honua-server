import FeatureLayer from "@arcgis/core/layers/FeatureLayer";
import Query from "@arcgis/core/rest/support/Query";

const parcels = new FeatureLayer({
  url: "https://example.test/rest/services/parcels/FeatureServer/0",
});

const query = new Query({
  where: "status = 'active'",
  outFields: ["OBJECTID", "status"],
  returnGeometry: true,
  orderByFields: ["OBJECTID DESC"],
  num: 50,
  start: 0,
});

void parcels.queryFeatures(query);
