import { describe, expect, it } from "vitest";

import {
  HonuaClient,
  HonuaFeatureLayer,
  HonuaMapLayer,
  HonuaMapService,
  HonuaOgcFeatureCollection,
  HonuaOgcFeatures,
  type HonuaQueryResponse,
  type HonuaApplyEditsResponse,
  type HonuaLayerMetadata,
  type HonuaServiceMetadata,
  type HonuaRelatedRecordsResponse,
  type HonuaExportMapResponse,
  type HonuaLegendResponse,
  type HonuaIdentifyResponse,
  type HonuaFindResponse,
  type HonuaOgcFeatureCollectionResponse,
  type HonuaOgcFeatureResponse,
  type HonuaTypedQueryResponse,
  type HonuaTypedFeature,
  type HonuaAttachmentListResponse,
  type HonuaQueryAttachmentsResponse,
} from "../src/index.js";

function createMockClient(responseBody: unknown): HonuaClient {
  return new HonuaClient({
    baseUrl: "https://example.test",
    fetchFn: async () =>
      new Response(JSON.stringify(responseBody), { status: 200 }),
  });
}

describe("Typed response models", () => {
  it("queryFeatures returns HonuaQueryResponse", async () => {
    const body: HonuaQueryResponse = {
      objectIdFieldName: "OBJECTID",
      geometryType: "esriGeometryPoint",
      spatialReference: { wkid: 4326 },
      fields: [{ name: "OBJECTID", type: "esriFieldTypeOID" }],
      features: [{ attributes: { OBJECTID: 1 }, geometry: { x: 0, y: 0 } }],
      exceededTransferLimit: false,
    };
    const client = createMockClient(body);
    const result = await client.queryFeatures({
      serviceId: "parcels",
      layerId: 0,
    });

    expect(result.objectIdFieldName).toBe("OBJECTID");
    expect(result.features?.[0].attributes.OBJECTID).toBe(1);
    expect(result.spatialReference?.wkid).toBe(4326);
  });

  it("applyEdits returns HonuaApplyEditsResponse", async () => {
    const body: HonuaApplyEditsResponse = {
      addResults: [{ objectId: 42, success: true }],
      updateResults: [],
      deleteResults: [],
    };
    const client = createMockClient(body);
    const result = await client.applyEdits({
      serviceId: "parcels",
      layerId: 0,
      adds: [{ attributes: { name: "test" } }],
    });

    expect(result.addResults?.[0].objectId).toBe(42);
    expect(result.addResults?.[0].success).toBe(true);
  });

  it("getLayerMetadata returns HonuaLayerMetadata", async () => {
    const body: HonuaLayerMetadata = {
      id: 0,
      name: "Parcels",
      type: "Feature Layer",
      geometryType: "esriGeometryPolygon",
      maxRecordCount: 2000,
      fields: [{ name: "OBJECTID", type: "esriFieldTypeOID" }],
    };
    const client = createMockClient(body);
    const result = await client.getLayerMetadata("parcels", 0);

    expect(result.name).toBe("Parcels");
    expect(result.geometryType).toBe("esriGeometryPolygon");
    expect(result.fields?.[0].name).toBe("OBJECTID");
  });

  it("getFeatureServiceMetadata returns HonuaServiceMetadata", async () => {
    const body: HonuaServiceMetadata = {
      serviceDescription: "Test service",
      layers: [{ id: 0, name: "Parcels" }],
      tables: [],
      maxRecordCount: 2000,
    };
    const client = createMockClient(body);
    const result = await client.getFeatureServiceMetadata("parcels");

    expect(result.serviceDescription).toBe("Test service");
    expect(result.layers?.[0].name).toBe("Parcels");
  });

  it("getMapServiceMetadata returns HonuaServiceMetadata", async () => {
    const body: HonuaServiceMetadata = {
      serviceDescription: "Map service",
      layers: [{ id: 0, name: "Roads" }],
    };
    const client = createMockClient(body);
    const result = await client.getMapServiceMetadata("roads");

    expect(result.serviceDescription).toBe("Map service");
    expect(result.layers?.[0].name).toBe("Roads");
  });

  it("queryRelatedRecords returns HonuaRelatedRecordsResponse", async () => {
    const body: HonuaRelatedRecordsResponse = {
      relatedRecordGroups: [
        {
          objectId: 1,
          relatedRecords: [{ attributes: { OBJECTID: 10, name: "related" } }],
        },
      ],
      fields: [{ name: "OBJECTID", type: "esriFieldTypeOID" }],
    };
    const client = createMockClient(body);
    const result = await client.queryRelatedRecords({
      serviceId: "parcels",
      layerId: 0,
      relationshipId: 1,
    });

    expect(result.relatedRecordGroups?.[0].objectId).toBe(1);
    expect(result.relatedRecordGroups?.[0].relatedRecords?.[0].attributes.name).toBe("related");
  });

  it("exportMap returns HonuaExportMapResponse", async () => {
    const body: HonuaExportMapResponse = {
      href: "https://example.test/image.png",
      width: 800,
      height: 600,
      extent: { xmin: -180, ymin: -90, xmax: 180, ymax: 90 },
    };
    const client = createMockClient(body);
    const result = await client.exportMap({
      serviceId: "basemap",
      bbox: [-180, -90, 180, 90],
      size: [800, 600],
    });

    expect(result.href).toBe("https://example.test/image.png");
    expect(result.width).toBe(800);
  });

  it("getMapLegend returns HonuaLegendResponse", async () => {
    const body: HonuaLegendResponse = {
      layers: [
        {
          layerId: 0,
          layerName: "Roads",
          legend: [{ label: "Highway", contentType: "image/png" }],
        },
      ],
    };
    const client = createMockClient(body);
    const result = await client.getMapLegend({ serviceId: "basemap" });

    expect(result.layers?.[0].layerName).toBe("Roads");
    expect(result.layers?.[0].legend?.[0].label).toBe("Highway");
  });

  it("identifyMap returns HonuaIdentifyResponse", async () => {
    const body: HonuaIdentifyResponse = {
      results: [
        {
          layerId: 0,
          layerName: "Buildings",
          value: "42",
          attributes: { name: "City Hall" },
        },
      ],
    };
    const client = createMockClient(body);
    const result = await client.identifyMap({
      serviceId: "basemap",
      geometry: { x: 0, y: 0 },
      mapExtent: [-1, -1, 1, 1],
      imageDisplay: [800, 600, 96],
    });

    expect(result.results?.[0].layerName).toBe("Buildings");
    expect(result.results?.[0].attributes?.name).toBe("City Hall");
  });

  it("findMap returns HonuaFindResponse", async () => {
    const body: HonuaFindResponse = {
      results: [
        {
          layerId: 0,
          layerName: "Cities",
          foundFieldName: "NAME",
          value: "Portland",
          attributes: { NAME: "Portland", POP: 650000 },
        },
      ],
    };
    const client = createMockClient(body);
    const result = await client.findMap({
      serviceId: "basemap",
      searchText: "Portland",
    });

    expect(result.results?.[0].value).toBe("Portland");
    expect(result.results?.[0].foundFieldName).toBe("NAME");
  });

  it("listOgcItems returns HonuaOgcFeatureCollectionResponse", async () => {
    const body: HonuaOgcFeatureCollectionResponse = {
      type: "FeatureCollection",
      features: [
        {
          type: "Feature",
          id: "1",
          geometry: { type: "Point", coordinates: [0, 0] },
          properties: { name: "test" },
        },
      ],
      numberMatched: 1,
      numberReturned: 1,
    };
    const client = createMockClient(body);
    const result = await client.listOgcItems({
      collectionId: "parcels",
    });

    expect(result.type).toBe("FeatureCollection");
    expect(result.features[0].properties?.name).toBe("test");
    expect(result.numberMatched).toBe(1);
  });

  it("getOgcItem returns HonuaOgcFeatureResponse", async () => {
    const body: HonuaOgcFeatureResponse = {
      type: "Feature",
      id: "42",
      geometry: { type: "Point", coordinates: [1, 2] },
      properties: { name: "parcel-42" },
    };
    const client = createMockClient(body);
    const result = await client.getOgcItem({
      collectionId: "parcels",
      featureId: "42",
    });

    expect(result.type).toBe("Feature");
    expect(result.id).toBe("42");
    expect(result.properties?.name).toBe("parcel-42");
  });
});

describe("Typed surface methods", () => {
  it("HonuaFeatureLayer.queryRelatedRecords returns typed response", async () => {
    const body: HonuaRelatedRecordsResponse = {
      relatedRecordGroups: [{ objectId: 1 }],
    };
    const client = createMockClient(body);
    const layer = client.featureLayer("test", 0);
    const result = await layer.queryRelatedRecords({ relationshipId: 1 });

    expect(result.relatedRecordGroups?.[0].objectId).toBe(1);
  });

  it("HonuaMapService.exportMap returns typed response", async () => {
    const body: HonuaExportMapResponse = {
      href: "https://example.test/export.png",
      width: 1024,
      height: 768,
    };
    const client = createMockClient(body);
    const mapSvc = new HonuaMapService({
      client,
      serviceId: "basemap",
    });
    const result = await mapSvc.exportMap({
      bbox: [0, 0, 1, 1],
      size: [1024, 768],
    });

    expect(result.href).toBe("https://example.test/export.png");
    expect(result.width).toBe(1024);
  });

  it("HonuaMapService.legend returns typed response", async () => {
    const body: HonuaLegendResponse = {
      layers: [{ layerId: 0, layerName: "Test" }],
    };
    const client = createMockClient(body);
    const mapSvc = new HonuaMapService({
      client,
      serviceId: "basemap",
    });
    const result = await mapSvc.legend();

    expect(result.layers?.[0].layerName).toBe("Test");
  });

  it("HonuaMapService.identify returns typed response", async () => {
    const body: HonuaIdentifyResponse = {
      results: [{ layerId: 0, value: "test" }],
    };
    const client = createMockClient(body);
    const mapSvc = new HonuaMapService({
      client,
      serviceId: "basemap",
    });
    const result = await mapSvc.identify({
      geometry: { x: 0, y: 0 },
      mapExtent: [0, 0, 1, 1],
      imageDisplay: [800, 600, 96],
    });

    expect(result.results?.[0].value).toBe("test");
  });

  it("HonuaMapService.find returns typed response", async () => {
    const body: HonuaFindResponse = {
      results: [{ layerId: 0, value: "Portland" }],
    };
    const client = createMockClient(body);
    const mapSvc = new HonuaMapService({
      client,
      serviceId: "basemap",
    });
    const result = await mapSvc.find({ searchText: "Portland" });

    expect(result.results?.[0].value).toBe("Portland");
  });

  it("HonuaMapLayer.metadata returns HonuaLayerMetadata", async () => {
    const body: HonuaLayerMetadata = {
      id: 0,
      name: "Roads",
      geometryType: "esriGeometryPolyline",
    };
    const client = createMockClient(body);
    const layer = new HonuaMapLayer({
      client,
      serviceId: "basemap",
      layerId: 0,
    });
    const result = await layer.metadata();

    expect(result.name).toBe("Roads");
    expect(result.geometryType).toBe("esriGeometryPolyline");
  });

  it("HonuaMapLayer.queryFeaturesAll returns HonuaFeature[]", async () => {
    const body: HonuaQueryResponse = {
      features: [
        { attributes: { OBJECTID: 1 }, geometry: { x: 0, y: 0 } },
        { attributes: { OBJECTID: 2 }, geometry: { x: 1, y: 1 } },
      ],
    };
    const client = createMockClient(body);
    const layer = new HonuaMapLayer({
      client,
      serviceId: "basemap",
      layerId: 0,
    });
    const features = await layer.queryFeaturesAll();

    expect(features).toHaveLength(2);
    expect(features[0].attributes.OBJECTID).toBe(1);
    expect(features[1].geometry).toEqual({ x: 1, y: 1 });
  });

  it("OGC createItem returns HonuaOgcFeatureResponse", async () => {
    const body: HonuaOgcFeatureResponse = {
      type: "Feature",
      id: "new-1",
      geometry: null,
      properties: { name: "created" },
    };
    const client = createMockClient(body);
    const ogc = new HonuaOgcFeatures({ client });
    const result = await ogc.createItem({
      collectionId: "test",
      feature: { type: "Feature", geometry: null, properties: { name: "created" } },
    });

    expect(result.type).toBe("Feature");
    expect(result.id).toBe("new-1");
  });

  it("OGC collection replaceItem returns HonuaOgcFeatureResponse", async () => {
    const body: HonuaOgcFeatureResponse = {
      type: "Feature",
      id: "42",
      geometry: null,
      properties: { name: "replaced" },
    };
    const client = createMockClient(body);
    const collection = new HonuaOgcFeatureCollection({
      client,
      collectionId: "parcels",
    });
    const result = await collection.replaceItem({
      featureId: "42",
      feature: { type: "Feature", geometry: null, properties: { name: "replaced" } },
    });

    expect(result.type).toBe("Feature");
    expect(result.properties?.name).toBe("replaced");
  });
});

describe("Schema-aware typed collections (Direction 10)", () => {
  interface ParcelAttributes {
    parcel_id: string;
    area: number;
    zoning: string;
  }

  it("featureLayer<T> returns typed query response with attribute autocompletion", async () => {
    const body: HonuaTypedQueryResponse<ParcelAttributes> = {
      objectIdFieldName: "OBJECTID",
      features: [
        { attributes: { parcel_id: "P001", area: 1500, zoning: "R1" } },
        { attributes: { parcel_id: "P002", area: 2200, zoning: "C2" } },
      ],
    };
    const client = createMockClient(body);
    const parcels = client.featureLayer<ParcelAttributes>("parcels", 0);
    const result = await parcels.queryFeatures({ where: "area > 1000" });

    // These accesses are fully typed — no casts needed
    expect(result.features?.[0].attributes.parcel_id).toBe("P001");
    expect(result.features?.[0].attributes.area).toBe(1500);
    expect(result.features?.[1].attributes.zoning).toBe("C2");
  });

  it("generic layer queryFeaturesAll returns typed features", async () => {
    const body = {
      features: [
        { attributes: { parcel_id: "P001", area: 1500, zoning: "R1" } },
      ],
    };
    const client = createMockClient(body);
    const parcels = client.featureLayer<ParcelAttributes>("parcels", 0);
    const features = await parcels.queryFeaturesAll();

    expect(features[0].attributes.parcel_id).toBe("P001");
  });

  it("unparameterized featureLayer defaults to Record<string, unknown>", async () => {
    const body = {
      features: [{ attributes: { OBJECTID: 1, name: "test" } }],
    };
    const client = createMockClient(body);
    const layer = client.featureLayer("test", 0);
    const result = await layer.queryFeatures();

    // attributes is Record<string, unknown> — access is untyped but doesn't error
    expect(result.features?.[0].attributes.OBJECTID).toBe(1);
  });

  it("service.featureLayer<T> propagates generic parameter", async () => {
    const body = {
      features: [
        { attributes: { parcel_id: "P003", area: 800, zoning: "A1" } },
      ],
    };
    const client = createMockClient(body);
    const service = client.service("parcels");
    const parcels = service.featureLayer<ParcelAttributes>(0);
    const result = await parcels.queryFeatures();

    expect(result.features?.[0].attributes.zoning).toBe("A1");
  });

  it("HonuaTypedFeature structural shape is compatible with HonuaFeature", () => {
    // Verify structural compatibility: HonuaTypedFeature<Record<string, unknown>>
    // should be assignable to HonuaFeature
    const typed: HonuaTypedFeature = {
      attributes: { id: 1, name: "test" },
      geometry: { type: "Point", coordinates: [0, 0] },
    };
    const feature: HonuaTypedFeature<Record<string, unknown>> = typed;
    expect(feature.attributes.id).toBe(1);
  });
});
