import FeatureSet from "@arcgis/core/rest/support/FeatureSet";

const set = new FeatureSet({
  fields: [{ name: "OBJECTID", type: "oid" }],
  features: [{ attributes: { OBJECTID: 1 } }],
  geometryType: "esriGeometryPoint",
  objectIdFieldName: "OBJECTID",
});

void set;
