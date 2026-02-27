import esriConfig from "@arcgis/core/config";

esriConfig.apiKey = "demo-key";
esriConfig.portalUrl = "https://portal.example.test";
esriConfig.request.interceptors.push({
  urls: "services/parcels",
  before(params) {
    params.requestOptions.headers = {
      ...(params.requestOptions.headers ?? {}),
      Authorization: "Bearer token",
    };
  },
});
