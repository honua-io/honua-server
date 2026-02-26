import request from "@arcgis/core/request";

void request("https://example.test/rest/services/demo/FeatureServer/0", {
  responseType: "json",
  query: {
    f: "json",
  },
});
