import FeatureLayer from "@arcgis/core/layers/FeatureLayer";

const layer = new FeatureLayer({
  url: "https://example.test/rest/services/default/FeatureServer/0",
});

async function runRelatedQuery() {
  return layer.queryRelatedFeatures({
    relationshipId: 1,
    objectIds: [42],
    outFields: ["OBJECTID", "NAME"],
    returnGeometry: false,
  });
}

void runRelatedQuery();
