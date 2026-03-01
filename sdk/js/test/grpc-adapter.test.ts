import { describe, expect, it } from "vitest";
import { create } from "@bufbuild/protobuf";
import {
  toProtoQueryRequest,
  fromProtoQueryResponse,
  streamProtoPages,
} from "../src/core/grpc-adapter.js";
import {
  QueryFeaturesResponseSchema,
  FeaturePageSchema,
  FeatureSchema,
  AttributeValueSchema,
  GeometrySchema,
  PointGeometrySchema,
  PolylineGeometrySchema,
  PolygonGeometrySchema,
  MultiPointGeometrySchema,
  CoordinateSequenceSchema,
  CoordinateSchema,
  SpatialReferenceSchema,
  FieldDefinitionSchema,
  ExtentSchema,
  FieldType,
  GeometryType,
  NullValue,
} from "../src/gen/honua/v1/feature_service_pb.js";

describe("toProtoQueryRequest", () => {
  it("converts a minimal request", () => {
    const result = toProtoQueryRequest({
      serviceId: "svc1",
      layerId: 0,
    });

    expect(result.serviceId).toBe("svc1");
    expect(result.layerId).toBe(0);
    expect(result.where).toBe("1=1");
    expect(result.returnGeometry).toBe(true);
  });

  it("converts where clause and outFields", () => {
    const result = toProtoQueryRequest({
      serviceId: "test",
      layerId: 1,
      where: "name = 'A'",
      outFields: ["name", "value"],
      returnGeometry: false,
    });

    expect(result.where).toBe("name = 'A'");
    expect(result.outFields).toEqual(["name", "value"]);
    expect(result.returnGeometry).toBe(false);
  });

  it("converts string outFields to array", () => {
    const result = toProtoQueryRequest({
      serviceId: "test",
      layerId: 0,
      outFields: "name, value, status",
    });

    expect(result.outFields).toEqual(["name", "value", "status"]);
  });

  it("converts objectIds from array", () => {
    const result = toProtoQueryRequest({
      serviceId: "test",
      layerId: 0,
      objectIds: [1, 2, 3],
    });

    expect(result.objectIds).toEqual([1n, 2n, 3n]);
  });

  it("converts objectIds from string", () => {
    const result = toProtoQueryRequest({
      serviceId: "test",
      layerId: 0,
      objectIds: "10,20,30",
    });

    expect(result.objectIds).toEqual([10n, 20n, 30n]);
  });

  it("converts pagination parameters", () => {
    const result = toProtoQueryRequest({
      serviceId: "test",
      layerId: 0,
      resultOffset: 100,
      resultRecordCount: 50,
    });

    expect(result.resultOffset).toBe(100);
    expect(result.resultRecordCount).toBe(50);
  });

  it("converts orderByFields and returnDistinct", () => {
    const result = toProtoQueryRequest({
      serviceId: "test",
      layerId: 0,
      orderByFields: "name ASC",
      returnDistinctValues: true,
    });

    expect(result.orderBy).toBe("name ASC");
    expect(result.returnDistinct).toBe(true);
  });
});

describe("fromProtoQueryResponse", () => {
  it("converts a standard feature response", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const nameAttr = create(AttributeValueSchema);
    nameAttr.value = { case: "stringValue", value: "TestName" };
    feature.attributes["name"] = nameAttr;

    const geometry = create(GeometrySchema);
    const point = create(PointGeometrySchema);
    point.x = -157.8;
    point.y = 21.3;
    geometry.shape = { case: "point", value: point };
    feature.geometry = geometry;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.geometryType = GeometryType.POINT;

    const sr = create(SpatialReferenceSchema);
    sr.wkid = 4326;
    response.spatialReference = sr;

    const field = create(FieldDefinitionSchema);
    field.name = "name";
    field.fieldType = FieldType.STRING;
    field.length = 255;
    field.nullable = true;
    response.fields = [field];

    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.objectIdFieldName).toBe("objectid");
    expect(result.geometryType).toBe("esriGeometryPoint");
    expect(result.spatialReference).toEqual({ wkid: 4326 });
    expect(result.fields).toHaveLength(1);
    expect(result.fields[0].name).toBe("name");
    expect(result.fields[0].type).toBe("esriFieldTypeString");
    expect(result.features).toHaveLength(1);
    expect(result.features[0].attributes.name).toBe("TestName");
    expect(result.features[0].geometry).toEqual({ x: -157.8, y: 21.3 });
  });

  it("converts count-only response", () => {
    const response = create(QueryFeaturesResponseSchema);
    response.count = 42n;

    const result = fromProtoQueryResponse(response) as any;

    expect(result.count).toBe(42);
    expect(result.features).toBeUndefined();
  });

  it("converts ids-only response", () => {
    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.objectIds = [1n, 2n, 3n];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.objectIdFieldName).toBe("objectid");
    expect(result.objectIds).toEqual([1, 2, 3]);
    expect(result.features).toBeUndefined();
  });

  it("converts extent-only response", () => {
    const response = create(QueryFeaturesResponseSchema);
    const ext = create(ExtentSchema);
    ext.xmin = -10;
    ext.ymin = -20;
    ext.xmax = 30;
    ext.ymax = 40;
    const extSr = create(SpatialReferenceSchema);
    extSr.wkid = 4326;
    ext.spatialReference = extSr;
    response.extent = ext;

    const result = fromProtoQueryResponse(response) as any;

    expect(result.extent.xmin).toBe(-10);
    expect(result.extent.ymin).toBe(-20);
    expect(result.extent.xmax).toBe(30);
    expect(result.extent.ymax).toBe(40);
    expect(result.extent.spatialReference.wkid).toBe(4326);
  });

  it("converts null attribute values", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const nullAttr = create(AttributeValueSchema);
    nullAttr.value = { case: "nullValue", value: NullValue.NULL_VALUE };
    feature.attributes["status"] = nullAttr;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.features[0].attributes.status).toBeNull();
  });

  it("converts numeric attribute types", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const int32Attr = create(AttributeValueSchema);
    int32Attr.value = { case: "int32Value", value: 42 };
    feature.attributes["count"] = int32Attr;

    const int64Attr = create(AttributeValueSchema);
    int64Attr.value = { case: "int64Value", value: 9007199254740991n };
    feature.attributes["bigId"] = int64Attr;

    const doubleAttr = create(AttributeValueSchema);
    doubleAttr.value = { case: "doubleValue", value: 3.14 };
    feature.attributes["ratio"] = doubleAttr;

    const floatAttr = create(AttributeValueSchema);
    floatAttr.value = { case: "floatValue", value: 2.5 };
    feature.attributes["score"] = floatAttr;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.features[0].attributes.count).toBe(42);
    expect(result.features[0].attributes.bigId).toBe(9007199254740991);
    expect(result.features[0].attributes.ratio).toBe(3.14);
    expect(result.features[0].attributes.score).toBe(2.5);
  });

  it("converts boolean and datetime attributes", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const boolAttr = create(AttributeValueSchema);
    boolAttr.value = { case: "boolValue", value: true };
    feature.attributes["active"] = boolAttr;

    const dtAttr = create(AttributeValueSchema);
    dtAttr.value = { case: "datetimeValue", value: 1704067200000n };
    feature.attributes["created"] = dtAttr;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.features[0].attributes.active).toBe(true);
    expect(result.features[0].attributes.created).toBe(1704067200000);
  });

  it("converts polyline geometry", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const geometry = create(GeometrySchema);
    const polyline = create(PolylineGeometrySchema);
    const path = create(CoordinateSequenceSchema);
    const c1 = create(CoordinateSchema);
    c1.x = 0;
    c1.y = 0;
    const c2 = create(CoordinateSchema);
    c2.x = 10;
    c2.y = 10;
    path.coords = [c1, c2];
    polyline.paths = [path];
    geometry.shape = { case: "polyline", value: polyline };
    feature.geometry = geometry;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.geometryType = GeometryType.LINE_STRING;
    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.features[0].geometry).toEqual({
      paths: [[[0, 0], [10, 10]]],
    });
  });

  it("converts polygon geometry", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const geometry = create(GeometrySchema);
    const polygon = create(PolygonGeometrySchema);
    const ring = create(CoordinateSequenceSchema);
    const coords = [
      [0, 0], [10, 0], [10, 10], [0, 10], [0, 0],
    ].map(([x, y]) => {
      const c = create(CoordinateSchema);
      c.x = x;
      c.y = y;
      return c;
    });
    ring.coords = coords;
    polygon.rings = [ring];
    geometry.shape = { case: "polygon", value: polygon };
    feature.geometry = geometry;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.geometryType = GeometryType.POLYGON;
    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.features[0].geometry).toEqual({
      rings: [[[0, 0], [10, 0], [10, 10], [0, 10], [0, 0]]],
    });
  });

  it("converts multipoint geometry", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const geometry = create(GeometrySchema);
    const multiPoint = create(MultiPointGeometrySchema);
    const p1 = create(PointGeometrySchema);
    p1.x = 1;
    p1.y = 2;
    const p2 = create(PointGeometrySchema);
    p2.x = 3;
    p2.y = 4;
    multiPoint.points = [p1, p2];
    geometry.shape = { case: "multiPoint", value: multiPoint };
    feature.geometry = geometry;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.geometryType = GeometryType.MULTI_POINT;
    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.features[0].geometry).toEqual({
      points: [[1, 2], [3, 4]],
    });
  });

  it("handles feature with no geometry", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const nameAttr = create(AttributeValueSchema);
    nameAttr.value = { case: "stringValue", value: "NoGeom" };
    feature.attributes["name"] = nameAttr;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.features[0].geometry).toBeUndefined();
    expect(result.features[0].attributes.name).toBe("NoGeom");
  });

  it("maps field types correctly", () => {
    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";

    const fieldTypes: Array<[FieldType, string]> = [
      [FieldType.STRING, "esriFieldTypeString"],
      [FieldType.INTEGER, "esriFieldTypeInteger"],
      [FieldType.BIG_INTEGER, "esriFieldTypeInteger"],
      [FieldType.DOUBLE, "esriFieldTypeDouble"],
      [FieldType.FLOAT, "esriFieldTypeSingle"],
      [FieldType.BOOLEAN, "esriFieldTypeSmallInteger"],
      [FieldType.DATE_TIME, "esriFieldTypeDate"],
      [FieldType.BINARY, "esriFieldTypeBlob"],
      [FieldType.UUID, "esriFieldTypeGUID"],
    ];

    response.fields = fieldTypes.map(([ft], i) => {
      const f = create(FieldDefinitionSchema);
      f.name = `field_${i}`;
      f.fieldType = ft;
      return f;
    });

    const result = fromProtoQueryResponse(response) as any;

    fieldTypes.forEach(([, expectedType], i) => {
      expect(result.fields[i].type).toBe(expectedType);
    });
  });

  it("sets exceededTransferLimit when true", () => {
    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.exceededTransferLimit = true;
    response.features = [create(FeatureSchema)];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.exceededTransferLimit).toBe(true);
  });

  it("omits exceededTransferLimit when false", () => {
    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.exceededTransferLimit = false;
    response.features = [create(FeatureSchema)];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.exceededTransferLimit).toBeUndefined();
  });

  it("converts point geometry with z and m", () => {
    const feature = create(FeatureSchema);
    feature.id = 1n;

    const geometry = create(GeometrySchema);
    const point = create(PointGeometrySchema);
    point.x = 10;
    point.y = 20;
    point.z = 100;
    point.m = 0.5;
    geometry.shape = { case: "point", value: point };
    feature.geometry = geometry;

    const response = create(QueryFeaturesResponseSchema);
    response.objectIdFieldName = "objectid";
    response.features = [feature];

    const result = fromProtoQueryResponse(response) as any;

    expect(result.features[0].geometry).toEqual({ x: 10, y: 20, z: 100, m: 0.5 });
  });
});

describe("streamProtoPages", () => {
  it("yields feature arrays from pages", async () => {
    const page1 = create(FeaturePageSchema);
    const f1 = create(FeatureSchema);
    f1.id = 1n;
    const f2 = create(FeatureSchema);
    f2.id = 2n;
    page1.features = [f1, f2];
    page1.isLastPage = false;

    const page2 = create(FeaturePageSchema);
    const f3 = create(FeatureSchema);
    f3.id = 3n;
    page2.features = [f3];
    page2.isLastPage = true;

    async function* mockStream() {
      yield page1;
      yield page2;
    }

    const pages: unknown[][] = [];
    for await (const batch of streamProtoPages(mockStream())) {
      pages.push(batch);
    }

    expect(pages).toHaveLength(2);
    expect(pages[0]).toHaveLength(2);
    expect(pages[1]).toHaveLength(1);
  });

  it("stops on empty last page", async () => {
    const page1 = create(FeaturePageSchema);
    const f1 = create(FeatureSchema);
    f1.id = 1n;
    page1.features = [f1];
    page1.isLastPage = false;

    const page2 = create(FeaturePageSchema);
    page2.features = [];
    page2.isLastPage = true;

    async function* mockStream() {
      yield page1;
      yield page2;
    }

    const pages: unknown[][] = [];
    for await (const batch of streamProtoPages(mockStream())) {
      pages.push(batch);
    }

    expect(pages).toHaveLength(1);
  });

  it("handles single page stream", async () => {
    const page = create(FeaturePageSchema);
    const f1 = create(FeatureSchema);
    f1.id = 1n;
    page.features = [f1];
    page.isLastPage = true;

    async function* mockStream() {
      yield page;
    }

    const pages: unknown[][] = [];
    for await (const batch of streamProtoPages(mockStream())) {
      pages.push(batch);
    }

    expect(pages).toHaveLength(1);
    expect(pages[0]).toHaveLength(1);
  });

  it("handles empty stream", async () => {
    const page = create(FeaturePageSchema);
    page.features = [];
    page.isLastPage = true;

    async function* mockStream() {
      yield page;
    }

    const pages: unknown[][] = [];
    for await (const batch of streamProtoPages(mockStream())) {
      pages.push(batch);
    }

    expect(pages).toHaveLength(0);
  });
});
