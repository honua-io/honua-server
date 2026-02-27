import { HonuaClient } from "__HONUA_ENTRY__";

const fetchFn = globalThis.__honuaFetchFn;
if (typeof fetchFn !== "function") {
  throw new Error("Fixture requires globalThis.__honuaFetchFn to be a function.");
}

const client = new HonuaClient({
  baseUrl: "https://example.test",
  fetchFn,
});

const service = client.service("transport");
const layer = service.featureLayer(0);
const mapService = service.mapService();
const ogc = client.ogcFeatures();
const parcels = ogc.collection("parcels");

const serviceMetadata = await service.featureServiceMetadata();
const layerMetadata = await layer.metadata();
const layerQuery = await layer.queryFeatures({
  where: "status = 'active'",
  outFields: ["OBJECTID", "NAME"],
  returnGeometry: false,
});
const layerCount = await layer.queryFeatureCount({
  where: "status = 'active'",
});
const legend = await mapService.legend();
const serviceRequestQuery = await service.request({
  path: "FeatureServer/0/query",
  query: {
    where: "status = 'active'",
    outFields: "OBJECTID",
    returnGeometry: false,
  },
});

const landing = await ogc.landing();
const collections = await ogc.collections();
const ogcItems = await parcels.items({
  limit: 2,
  properties: ["id", "status"],
});
const ogcSingle = await parcels.item({
  featureId: "parcel-1",
});
const ogcCreated = await parcels.createItem({
  feature: {
    type: "Feature",
    properties: {
      status: "active",
      source: "dual-protocol-fixture",
    },
    geometry: null,
  },
});
const ogcDeleted = await parcels.deleteItem({
  featureId: "parcel-3",
});

export default {
  serviceDescription: serviceMetadata?.serviceDescription ?? null,
  layerName: layerMetadata?.name ?? null,
  layerQueryCount: Array.isArray(layerQuery?.features) ? layerQuery.features.length : 0,
  layerCount,
  legendLayerCount: Array.isArray(legend?.layers) ? legend.layers.length : 0,
  serviceRequestCount: Array.isArray(serviceRequestQuery?.features) ? serviceRequestQuery.features.length : 0,
  ogcTitle: landing?.title ?? null,
  ogcCollectionCount: Array.isArray(collections?.collections) ? collections.collections.length : 0,
  ogcItemsCount: Array.isArray(ogcItems?.features) ? ogcItems.features.length : 0,
  ogcFirstItemId: ogcItems?.features?.[0]?.id ?? null,
  ogcSingleId: ogcSingle?.id ?? null,
  ogcCreatedId: ogcCreated?.id ?? null,
  ogcDeleteStatus: ogcDeleted?.status ?? null,
};
