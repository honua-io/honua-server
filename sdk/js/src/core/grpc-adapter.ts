import { create } from "@bufbuild/protobuf";
import {
  type AttributeValue,
  type FeaturePage,
  FieldType,
  GeometryType,
  type Feature as ProtoFeature,
  type FieldDefinition as ProtoFieldDefinition,
  QueryFeaturesRequestSchema,
  type QueryFeaturesResponse,
} from "../gen/honua/v1/feature_service_pb.js";
import { HonuaGrpcError } from "./errors.js";
import type {
  HonuaCountResponse,
  HonuaExtentResponse,
  HonuaFeature,
  HonuaFieldInfo,
  HonuaObjectIdsResponse,
  HonuaQueryResponse,
  HonuaSpatialReference,
  QueryFeaturesRequest,
} from "./types.js";

/**
 * Maps proto FieldType enum values to Esri-style field type strings
 * used in the JSON response shape.
 */
const FIELD_TYPE_MAP: Record<number, string> = {
  [FieldType.STRING]: "esriFieldTypeString",
  [FieldType.INTEGER]: "esriFieldTypeInteger",
  [FieldType.BIG_INTEGER]: "esriFieldTypeInteger",
  [FieldType.DOUBLE]: "esriFieldTypeDouble",
  [FieldType.FLOAT]: "esriFieldTypeSingle",
  [FieldType.BOOLEAN]: "esriFieldTypeSmallInteger",
  [FieldType.DATE_TIME]: "esriFieldTypeDate",
  [FieldType.DATE]: "esriFieldTypeDate",
  [FieldType.TIME]: "esriFieldTypeDate",
  [FieldType.GEOMETRY]: "esriFieldTypeGeometry",
  [FieldType.JSON]: "esriFieldTypeString",
  [FieldType.BINARY]: "esriFieldTypeBlob",
  [FieldType.UUID]: "esriFieldTypeGUID",
};

/**
 * Maps proto GeometryType enum values to Esri-style geometry type strings.
 */
const GEOMETRY_TYPE_MAP: Record<number, string> = {
  [GeometryType.POINT]: "esriGeometryPoint",
  [GeometryType.MULTI_POINT]: "esriGeometryMultipoint",
  [GeometryType.LINE_STRING]: "esriGeometryPolyline",
  [GeometryType.MULTI_LINE_STRING]: "esriGeometryPolyline",
  [GeometryType.POLYGON]: "esriGeometryPolygon",
  [GeometryType.MULTI_POLYGON]: "esriGeometryPolygon",
  [GeometryType.NONE]: "esriGeometryNull",
};

/**
 * Converts a SDK QueryFeaturesRequest into a proto QueryFeaturesRequest message.
 */
export function toProtoQueryRequest(request: QueryFeaturesRequest) {
  const msg = create(QueryFeaturesRequestSchema);
  msg.serviceId = request.serviceId;
  msg.layerId = request.layerId;
  msg.where = request.where ?? "1=1";
  msg.returnGeometry = request.returnGeometry ?? true;

  if (request.outFields !== undefined) {
    const fields =
      typeof request.outFields === "string"
        ? request.outFields
            .split(",")
            .map((f) => f.trim())
            .filter(Boolean)
        : request.outFields;
    msg.outFields = fields;
  }

  if (request.objectIds !== undefined) {
    const ids =
      typeof request.objectIds === "string"
        ? request.objectIds.split(",").map((id) => BigInt(id.trim()))
        : request.objectIds.map((id) => BigInt(id));
    msg.objectIds = ids;
  }

  if (request.resultOffset !== undefined) {
    msg.resultOffset = request.resultOffset;
  }
  if (request.resultRecordCount !== undefined) {
    msg.resultRecordCount = request.resultRecordCount;
  }
  if (request.orderByFields !== undefined) {
    msg.orderBy = request.orderByFields;
  }
  if (request.returnDistinctValues !== undefined) {
    msg.returnDistinct = request.returnDistinctValues;
  }

  return msg;
}

/**
 * Converts a proto QueryFeaturesResponse into the JSON-compatible shape
 * matching the `f=json` response format.
 */
export function fromProtoQueryResponse(
  response: QueryFeaturesResponse,
): HonuaQueryResponse | HonuaCountResponse | HonuaObjectIdsResponse | HonuaExtentResponse {
  // Count-only response
  if (response.count !== 0n && response.features.length === 0) {
    return { count: Number(response.count) };
  }

  // IDs-only response
  if (response.objectIds.length > 0 && response.features.length === 0) {
    return {
      objectIdFieldName: response.objectIdFieldName,
      objectIds: response.objectIds.map(Number),
    };
  }

  // Extent-only response
  if (response.extent && response.features.length === 0) {
    const ext = response.extent;
    return {
      extent: {
        xmin: ext.xmin,
        ymin: ext.ymin,
        xmax: ext.xmax,
        ymax: ext.ymax,
        spatialReference: ext.spatialReference ? convertSpatialReference(ext.spatialReference) : undefined,
      },
    };
  }

  // Standard feature response
  return {
    objectIdFieldName: response.objectIdFieldName,
    geometryType: GEOMETRY_TYPE_MAP[response.geometryType] ?? "esriGeometryPoint",
    spatialReference: response.spatialReference ? convertSpatialReference(response.spatialReference) : undefined,
    fields: response.fields.map(convertField),
    features: response.features.map(convertFeature),
    exceededTransferLimit: response.exceededTransferLimit || undefined,
  };
}

/**
 * Converts a stream of proto FeaturePages into an async generator
 * that yields arrays of JSON-compatible features, matching the
 * existing queryFeaturesStream yield type.
 */
export async function* streamProtoPages(
  stream: AsyncIterable<FeaturePage>,
): AsyncGenerator<HonuaFeature[], void, undefined> {
  try {
    for await (const page of stream) {
      if (page.features.length === 0 && page.isLastPage) {
        break;
      }
      const features = page.features.map(convertFeature);
      if (features.length > 0) {
        yield features;
      }
      if (page.isLastPage) {
        break;
      }
    }
  } catch (error) {
    throw wrapConnectError(error);
  }
}

/**
 * Wraps a ConnectError (or any error from the gRPC transport) in a
 * HonuaGrpcError for consistent `instanceof` discrimination.
 */
export function wrapConnectError(error: unknown): Error {
  if (error instanceof Error && "code" in error && typeof (error as Record<string, unknown>).code === "number") {
    return new HonuaGrpcError(
      (error as Record<string, unknown>).code as number,
      error.message,
      "rawMessage" in error ? (error as Record<string, unknown>).rawMessage : undefined,
    );
  }
  if (error instanceof Error) {
    return error;
  }
  return new Error(String(error));
}

function convertSpatialReference(sr: { wkid: number; latestWkid: number; wkt: string }): HonuaSpatialReference {
  const result: HonuaSpatialReference = {};
  if (sr.wkid !== 0) {
    result.wkid = sr.wkid;
  }
  if (sr.latestWkid !== 0) {
    result.latestWkid = sr.latestWkid;
  }
  if (sr.wkt) {
    result.wkt = sr.wkt;
  }
  return result;
}

function convertField(field: ProtoFieldDefinition): HonuaFieldInfo {
  return {
    name: field.name,
    type: FIELD_TYPE_MAP[field.fieldType] ?? "esriFieldTypeString",
    alias: field.name,
    length: field.length || undefined,
    nullable: field.nullable,
  };
}

function convertFeature(feature: ProtoFeature): HonuaFeature {
  const attributes: Record<string, unknown> = {};
  for (const [key, attrValue] of Object.entries(feature.attributes)) {
    attributes[key] = convertAttributeValue(attrValue);
  }

  const result: HonuaFeature = { attributes };

  if (feature.geometry) {
    result.geometry = convertGeometry(feature.geometry);
  }

  return result;
}

function convertAttributeValue(attr: AttributeValue): unknown {
  switch (attr.value.case) {
    case "stringValue":
      return attr.value.value;
    case "int32Value":
      return attr.value.value;
    case "int64Value":
      return Number(attr.value.value);
    case "doubleValue":
      return attr.value.value;
    case "floatValue":
      return attr.value.value;
    case "boolValue":
      return attr.value.value;
    case "datetimeValue":
      return Number(attr.value.value);
    case "bytesValue":
      return null;
    case "nullValue":
      return null;
    default:
      return null;
  }
}

function convertGeometry(geometry: NonNullable<ProtoFeature["geometry"]>): Record<string, unknown> | null {
  switch (geometry.shape.case) {
    case "point": {
      const p = geometry.shape.value;
      const result: Record<string, unknown> = { x: p.x, y: p.y };
      if (p.z !== undefined) result.z = p.z;
      if (p.m !== undefined) result.m = p.m;
      return result;
    }
    case "multiPoint": {
      const mp = geometry.shape.value;
      return {
        points: mp.points.map((p) => {
          const coords: number[] = [p.x, p.y];
          if (p.z !== undefined) coords.push(p.z);
          return coords;
        }),
      };
    }
    case "polyline": {
      const pl = geometry.shape.value;
      return {
        paths: pl.paths.map((path) =>
          path.coords.map((c) => {
            const coords: number[] = [c.x, c.y];
            if (c.z !== undefined) coords.push(c.z);
            return coords;
          }),
        ),
      };
    }
    case "polygon": {
      const pg = geometry.shape.value;
      return {
        rings: pg.rings.map((ring) =>
          ring.coords.map((c) => {
            const coords: number[] = [c.x, c.y];
            if (c.z !== undefined) coords.push(c.z);
            return coords;
          }),
        ),
      };
    }
    default:
      return null;
  }
}
