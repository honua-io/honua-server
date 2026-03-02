import { describe, expect, it } from "vitest";

import { decodePbfQueryResponse, isPbfResponse } from "../src/core/pbf-decoder.js";

// ── Protobuf encoding helpers for building test fixtures ──────

/** Encode a varint (unsigned 32-bit). */
function varint(value: number): number[] {
  const bytes: number[] = [];
  while (value > 0x7f) {
    bytes.push((value & 0x7f) | 0x80);
    value >>>= 7;
  }
  bytes.push(value & 0x7f);
  return bytes;
}

/** Encode a 64-bit varint from a JS number (safe for ints < 2^53). */
function varint64(value: number): number[] {
  const bytes: number[] = [];
  while (value > 0x7f) {
    bytes.push((value & 0x7f) | 0x80);
    value = Math.floor(value / 128);
  }
  bytes.push(value & 0x7f);
  return bytes;
}

/** Zigzag-encode a signed 32-bit integer. */
function zigzag32(n: number): number {
  return (n << 1) ^ (n >> 31);
}

/** Zigzag-encode a signed 64-bit integer (safe for ints < 2^52). */
function zigzag64(n: number): number {
  return n >= 0 ? n * 2 : -n * 2 - 1;
}

/** Tag byte for a field number + wire type. */
function tag(fieldNumber: number, wireType: number): number[] {
  return varint((fieldNumber << 3) | wireType);
}

/** Length-delimited field: tag + length varint + data. */
function lengthDelimited(fieldNumber: number, data: number[]): number[] {
  return [...tag(fieldNumber, 2), ...varint(data.length), ...data];
}

/** String field: tag + length varint + UTF-8 bytes. */
function stringField(fieldNumber: number, str: string): number[] {
  const bytes = Array.from(new TextEncoder().encode(str));
  return lengthDelimited(fieldNumber, bytes);
}

/** Varint field: tag + varint value. */
function varintField(fieldNumber: number, value: number): number[] {
  return [...tag(fieldNumber, 0), ...varint(value)];
}

/** Bool field: tag + varint(1). */
function boolField(fieldNumber: number, value: boolean): number[] {
  return varintField(fieldNumber, value ? 1 : 0);
}

/** Double field (wire type 1, 8 bytes little-endian). */
function doubleField(fieldNumber: number, value: number): number[] {
  const buf = new ArrayBuffer(8);
  new DataView(buf).setFloat64(0, value, true);
  return [...tag(fieldNumber, 1), ...new Uint8Array(buf)];
}

/** Float field (wire type 5, 4 bytes little-endian). */
function floatField(fieldNumber: number, value: number): number[] {
  const buf = new ArrayBuffer(4);
  new DataView(buf).setFloat32(0, value, true);
  return [...tag(fieldNumber, 5), ...new Uint8Array(buf)];
}

/** Packed repeated uint32. */
function packedUInt32(fieldNumber: number, values: number[]): number[] {
  const body: number[] = [];
  for (const v of values) body.push(...varint(v));
  return lengthDelimited(fieldNumber, body);
}

/** Packed repeated sint64 (zigzag). */
function packedSInt64(fieldNumber: number, values: number[]): number[] {
  const body: number[] = [];
  for (const v of values) body.push(...varint64(zigzag64(v)));
  return lengthDelimited(fieldNumber, body);
}

// ── High-level PBF message builders ──────────────────────────

function buildSpatialReference(wkid: number, latestWkid?: number): number[] {
  const sr = [...varintField(1, wkid)];
  if (latestWkid !== undefined) sr.push(...varintField(2, latestWkid));
  return sr;
}

function buildField(name: string, fieldType: number, alias?: string): number[] {
  const f = [
    ...stringField(1, name),
    ...varintField(2, fieldType),
  ];
  if (alias) f.push(...stringField(3, alias));
  return f;
}

function buildValue(opts: {
  stringValue?: string;
  floatValue?: number;
  doubleValue?: number;
  sintValue?: number;
  uintValue?: number;
  int64Value?: number;
  boolValue?: boolean;
  isNull?: boolean;
  fieldIndex: number;
}): number[] {
  const v: number[] = [];
  if (opts.stringValue !== undefined) v.push(...stringField(1, opts.stringValue));
  if (opts.floatValue !== undefined) v.push(...floatField(2, opts.floatValue));
  if (opts.doubleValue !== undefined) v.push(...doubleField(3, opts.doubleValue));
  if (opts.sintValue !== undefined) v.push(...tag(4, 0), ...varint(zigzag32(opts.sintValue)));
  if (opts.uintValue !== undefined) v.push(...varintField(5, opts.uintValue));
  if (opts.int64Value !== undefined) v.push(...tag(6, 0), ...varint64(opts.int64Value));
  if (opts.boolValue !== undefined) v.push(...boolField(9, opts.boolValue));
  if (opts.isNull) v.push(...boolField(10, true));
  v.push(...varintField(11, opts.fieldIndex));
  return v;
}

function buildPointGeometry(x: number, y: number): number[] {
  // For a single point with no transform: coords are raw sint64 delta-encoded
  // First (and only) delta pair is just (x, y) from (0, 0)
  return [
    ...varintField(1, 0), // geometryType=Point
    ...packedSInt64(3, [x, y]),
  ];
}

function buildTransform(
  xScale: number,
  yScale: number,
  xTranslate: number,
  yTranslate: number,
): number[] {
  const scale = [...doubleField(1, xScale), ...doubleField(2, yScale)];
  const translate = [...doubleField(1, xTranslate), ...doubleField(2, yTranslate)];
  return [
    ...lengthDelimited(2, scale),
    ...lengthDelimited(3, translate),
  ];
}

function buildFeature(values: number[][], geometry?: number[]): number[] {
  const f: number[] = [];
  for (const v of values) {
    f.push(...lengthDelimited(1, v));
  }
  if (geometry) f.push(...lengthDelimited(2, geometry));
  return f;
}

function buildFeatureResult(opts: {
  objectIdFieldName?: string;
  geometryType?: number;
  spatialReference?: number[];
  exceededTransferLimit?: boolean;
  transform?: number[];
  fields: number[][];
  features: number[][];
}): number[] {
  const fr: number[] = [];
  if (opts.objectIdFieldName) fr.push(...stringField(1, opts.objectIdFieldName));
  if (opts.geometryType !== undefined) fr.push(...varintField(7, opts.geometryType));
  if (opts.spatialReference) fr.push(...lengthDelimited(8, opts.spatialReference));
  if (opts.exceededTransferLimit) fr.push(...boolField(9, true));
  if (opts.transform) fr.push(...lengthDelimited(12, opts.transform));
  for (const field of opts.fields) {
    fr.push(...lengthDelimited(13, field));
  }
  for (const feature of opts.features) {
    fr.push(...lengthDelimited(15, feature));
  }
  return fr;
}

function buildFeatureCollectionPBuffer(version: string, featureResult: number[]): number[] {
  const queryResult = lengthDelimited(1, featureResult);
  return [
    ...stringField(1, version),
    ...lengthDelimited(2, queryResult),
  ];
}

function toBuffer(bytes: number[]): Uint8Array {
  return new Uint8Array(bytes);
}

// ── Tests ────────────────────────────────────────────────────

describe("isPbfResponse", () => {
  it("returns true for application/x-protobuf content type", () => {
    const response = new Response(null, {
      headers: { "Content-Type": "application/x-protobuf" },
    });
    expect(isPbfResponse(response)).toBe(true);
  });

  it("returns true for application/protobuf content type", () => {
    const response = new Response(null, {
      headers: { "Content-Type": "application/protobuf" },
    });
    expect(isPbfResponse(response)).toBe(true);
  });

  it("returns true when content type has extra params", () => {
    const response = new Response(null, {
      headers: { "Content-Type": "application/x-protobuf; charset=utf-8" },
    });
    expect(isPbfResponse(response)).toBe(true);
  });

  it("returns false for application/json", () => {
    const response = new Response(null, {
      headers: { "Content-Type": "application/json" },
    });
    expect(isPbfResponse(response)).toBe(false);
  });

  it("returns false for no content type", () => {
    const response = new Response(null);
    expect(isPbfResponse(response)).toBe(false);
  });
});

describe("decodePbfQueryResponse", () => {
  it("decodes empty feature result", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 0, // Point
      spatialReference: buildSpatialReference(4326),
      fields: [buildField("OBJECTID", 6)], // OID type = 6
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    expect(result).toMatchObject({
      objectIdFieldName: "OBJECTID",
      geometryType: "esriGeometryPoint",
      spatialReference: { wkid: 4326, latestWkid: 4326 },
      features: [],
    });
    expect((result.fields as unknown[])).toHaveLength(1);
  });

  it("decodes feature with string attribute", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 0,
      spatialReference: buildSpatialReference(4326),
      fields: [
        buildField("OBJECTID", 6),
        buildField("name", 4), // String = 4
      ],
      features: [
        buildFeature([
          buildValue({ uintValue: 1, fieldIndex: 0 }),
          buildValue({ stringValue: "Test Park", fieldIndex: 1 }),
        ]),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    expect(result.features).toHaveLength(1);
    const features = result.features as Record<string, unknown>[];
    const attrs = features[0].attributes as Record<string, unknown>;
    expect(attrs.OBJECTID).toBe(1);
    expect(attrs.name).toBe("Test Park");
  });

  it("decodes feature with numeric attributes", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [
        buildField("OBJECTID", 6),
        buildField("count", 1), // Integer
        buildField("area", 3),  // Double
      ],
      features: [
        buildFeature([
          buildValue({ uintValue: 42, fieldIndex: 0 }),
          buildValue({ sintValue: -5, fieldIndex: 1 }),
          buildValue({ doubleValue: 123.456, fieldIndex: 2 }),
        ]),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const attrs = features[0].attributes as Record<string, unknown>;
    expect(attrs.OBJECTID).toBe(42);
    expect(attrs.count).toBe(-5);
    expect(attrs.area).toBeCloseTo(123.456);
  });

  it("decodes feature with boolean attribute", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [
        buildField("OBJECTID", 6),
        buildField("active", 4),
      ],
      features: [
        buildFeature([
          buildValue({ uintValue: 1, fieldIndex: 0 }),
          buildValue({ boolValue: true, fieldIndex: 1 }),
        ]),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const attrs = features[0].attributes as Record<string, unknown>;
    expect(attrs.active).toBe(true);
  });

  it("decodes feature with null attribute", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [
        buildField("OBJECTID", 6),
        buildField("name", 4),
      ],
      features: [
        buildFeature([
          buildValue({ uintValue: 1, fieldIndex: 0 }),
          buildValue({ isNull: true, fieldIndex: 1 }),
        ]),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const attrs = features[0].attributes as Record<string, unknown>;
    expect(attrs.name).toBeNull();
  });

  it("decodes point geometry without transform", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 0,
      spatialReference: buildSpatialReference(4326),
      fields: [buildField("OBJECTID", 6)],
      features: [
        buildFeature(
          [buildValue({ uintValue: 1, fieldIndex: 0 })],
          buildPointGeometry(10, 20),
        ),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const geom = features[0].geometry as Record<string, unknown>;
    expect(geom.x).toBeCloseTo(10);
    expect(geom.y).toBeCloseTo(20);
  });

  it("decodes point geometry with transform (scale + translate)", () => {
    // Transform: scale=0.001, translate=100
    // Raw coord = 5000 → world = 5000 * 0.001 + 100 = 105
    const transform = buildTransform(0.001, 0.001, 100, 200);
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 0,
      spatialReference: buildSpatialReference(4326),
      transform,
      fields: [buildField("OBJECTID", 6)],
      features: [
        buildFeature(
          [buildValue({ uintValue: 1, fieldIndex: 0 })],
          buildPointGeometry(5000, 3000),
        ),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const geom = features[0].geometry as Record<string, unknown>;
    expect(geom.x).toBeCloseTo(105);
    expect(geom.y).toBeCloseTo(203);
  });

  it("decodes polyline geometry", () => {
    // Single path with 3 points: (0,0) → (10,5) → (20,10)
    // Delta-encoded: (0,0), (10,5), (10,5)
    const geometry = [
      ...varintField(1, 2), // Polyline
      ...packedUInt32(2, [3]), // lengths: one path of 3 coords
      ...packedSInt64(3, [0, 0, 10, 5, 10, 5]),
    ];
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 2, // Polyline
      spatialReference: buildSpatialReference(4326),
      fields: [buildField("OBJECTID", 6)],
      features: [
        buildFeature(
          [buildValue({ uintValue: 1, fieldIndex: 0 })],
          geometry,
        ),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const geom = features[0].geometry as Record<string, unknown>;
    const paths = geom.paths as number[][][];
    expect(paths).toHaveLength(1);
    expect(paths[0]).toHaveLength(3);
    expect(paths[0][0]).toEqual([0, 0]);
    expect(paths[0][1]).toEqual([10, 5]);
    expect(paths[0][2]).toEqual([20, 10]);
  });

  it("decodes polygon geometry with ring lengths", () => {
    // One ring with 4 points: (0,0) → (10,0) → (10,10) → (0,0)
    // Deltas: (0,0), (10,0), (0,10), (-10,-10)
    const geometry = [
      ...varintField(1, 3), // Polygon
      ...packedUInt32(2, [4]), // lengths: one ring of 4 coords
      ...packedSInt64(3, [0, 0, 10, 0, 0, 10, -10, -10]),
    ];
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 3, // Polygon
      spatialReference: buildSpatialReference(4326),
      fields: [buildField("OBJECTID", 6)],
      features: [
        buildFeature(
          [buildValue({ uintValue: 1, fieldIndex: 0 })],
          geometry,
        ),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const geom = features[0].geometry as Record<string, unknown>;
    const rings = geom.rings as number[][][];
    expect(rings).toHaveLength(1);
    expect(rings[0]).toHaveLength(4);
    expect(rings[0][0]).toEqual([0, 0]);
    expect(rings[0][1]).toEqual([10, 0]);
    expect(rings[0][2]).toEqual([10, 10]);
    expect(rings[0][3]).toEqual([0, 0]);
  });

  it("decodes multipoint geometry", () => {
    // Two points: (5,10) and (15,20)
    // Deltas: (5,10), (10,10)
    const geometry = [
      ...varintField(1, 1), // Multipoint
      ...packedSInt64(3, [5, 10, 10, 10]),
    ];
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 1,
      spatialReference: buildSpatialReference(4326),
      fields: [buildField("OBJECTID", 6)],
      features: [
        buildFeature(
          [buildValue({ uintValue: 1, fieldIndex: 0 })],
          geometry,
        ),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const geom = features[0].geometry as Record<string, unknown>;
    const points = geom.points as number[][];
    expect(points).toHaveLength(2);
    expect(points[0]).toEqual([5, 10]);
    expect(points[1]).toEqual([15, 20]);
  });

  it("decodes exceededTransferLimit=true", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      exceededTransferLimit: true,
      fields: [buildField("OBJECTID", 6)],
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    expect(result.exceededTransferLimit).toBe(true);
  });

  it("omits exceededTransferLimit when false", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [buildField("OBJECTID", 6)],
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    expect(result.exceededTransferLimit).toBeUndefined();
  });

  it("decodes multiple features", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 0,
      spatialReference: buildSpatialReference(4326),
      fields: [
        buildField("OBJECTID", 6),
        buildField("name", 4),
      ],
      features: [
        buildFeature([
          buildValue({ uintValue: 1, fieldIndex: 0 }),
          buildValue({ stringValue: "First", fieldIndex: 1 }),
        ]),
        buildFeature([
          buildValue({ uintValue: 2, fieldIndex: 0 }),
          buildValue({ stringValue: "Second", fieldIndex: 1 }),
        ]),
        buildFeature([
          buildValue({ uintValue: 3, fieldIndex: 0 }),
          buildValue({ stringValue: "Third", fieldIndex: 1 }),
        ]),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    expect(features).toHaveLength(3);
    expect((features[0].attributes as Record<string, unknown>).name).toBe("First");
    expect((features[1].attributes as Record<string, unknown>).name).toBe("Second");
    expect((features[2].attributes as Record<string, unknown>).name).toBe("Third");
  });

  it("maps field types to GeoServices names", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [
        buildField("oid", 6),        // OID
        buildField("name", 4),       // String
        buildField("count", 1),      // Integer
        buildField("val", 3),        // Double
        buildField("small", 0),      // SmallInteger
        buildField("guid", 10),      // GUID
        buildField("big", 13),       // BigInteger
      ],
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const fields = result.fields as Array<{ name: string; type: string; alias: string }>;
    expect(fields).toHaveLength(7);
    expect(fields[0].type).toBe("esriFieldTypeOID");
    expect(fields[1].type).toBe("esriFieldTypeString");
    expect(fields[2].type).toBe("esriFieldTypeInteger");
    expect(fields[3].type).toBe("esriFieldTypeDouble");
    expect(fields[4].type).toBe("esriFieldTypeSmallInteger");
    expect(fields[5].type).toBe("esriFieldTypeGUID");
    expect(fields[6].type).toBe("esriFieldTypeBigInteger");
  });

  it("decodes spatialReference with latestWkid", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 0,
      spatialReference: buildSpatialReference(102100, 3857),
      fields: [buildField("OBJECTID", 6)],
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const sr = result.spatialReference as { wkid: number; latestWkid: number };
    expect(sr.wkid).toBe(102100);
    expect(sr.latestWkid).toBe(3857);
  });

  it("accepts ArrayBuffer input as well as Uint8Array", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [buildField("OBJECTID", 6)],
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const buffer = toBuffer(pbf).buffer as ArrayBuffer;
    const result = decodePbfQueryResponse(buffer);

    expect(result.objectIdFieldName).toBe("OBJECTID");
  });

  it("decodes feature with no geometry when geometry not present", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [buildField("OBJECTID", 6)],
      features: [
        buildFeature([buildValue({ uintValue: 1, fieldIndex: 0 })]),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    expect(features[0].geometry).toBeUndefined();
    expect(features[0].attributes).toEqual({ OBJECTID: 1 });
  });

  it("decodes delta-encoded coordinates correctly across multiple points", () => {
    // Two features with different geometries; the delta resets per feature
    const transform = buildTransform(1, 1, 0, 0);
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 0,
      spatialReference: buildSpatialReference(4326),
      transform,
      fields: [buildField("OBJECTID", 6)],
      features: [
        buildFeature(
          [buildValue({ uintValue: 1, fieldIndex: 0 })],
          buildPointGeometry(-1225, 378),
        ),
        buildFeature(
          [buildValue({ uintValue: 2, fieldIndex: 0 })],
          buildPointGeometry(-734, 211),
        ),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const geom1 = features[0].geometry as Record<string, number>;
    const geom2 = features[1].geometry as Record<string, number>;
    expect(geom1.x).toBeCloseTo(-1225);
    expect(geom1.y).toBeCloseTo(378);
    expect(geom2.x).toBeCloseTo(-734);
    expect(geom2.y).toBeCloseTo(211);
  });

  it("decodes polyline with multiple paths", () => {
    // Two paths: path1 has 2 coords, path2 has 2 coords
    // Points: (0,0)→(10,10) | (20,20)→(30,30)
    // Deltas: (0,0),(10,10),(10,10),(10,10)  — delta continues across paths
    const geometry = [
      ...varintField(1, 2), // Polyline
      ...packedUInt32(2, [2, 2]), // two paths, each with 2 coords
      ...packedSInt64(3, [0, 0, 10, 10, 10, 10, 10, 10]),
    ];
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      geometryType: 2,
      spatialReference: buildSpatialReference(4326),
      fields: [buildField("OBJECTID", 6)],
      features: [
        buildFeature(
          [buildValue({ uintValue: 1, fieldIndex: 0 })],
          geometry,
        ),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const paths = (features[0].geometry as Record<string, unknown>).paths as number[][][];
    expect(paths).toHaveLength(2);
    expect(paths[0]).toEqual([[0, 0], [10, 10]]);
    expect(paths[1]).toEqual([[20, 20], [30, 30]]);
  });

  it("omits geometryType and spatialReference when not set", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [buildField("OBJECTID", 6)],
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    // geometryType is "" (empty string) which maps to esriGeometryNull → omitted
    expect(result.geometryType).toBeUndefined();
    expect(result.spatialReference).toBeUndefined();
  });

  it("decodes int64 attribute values", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [
        buildField("OBJECTID", 6),
        buildField("bignum", 13),
      ],
      features: [
        buildFeature([
          buildValue({ uintValue: 1, fieldIndex: 0 }),
          buildValue({ int64Value: 9007199254740000, fieldIndex: 1 }),
        ]),
      ],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const features = result.features as Record<string, unknown>[];
    const attrs = features[0].attributes as Record<string, unknown>;
    expect(attrs.bignum).toBe(9007199254740000);
  });

  it("decodes field alias correctly", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [
        buildField("OBJECTID", 6, "Object ID"),
        buildField("name", 4, "Park Name"),
      ],
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const fields = result.fields as Array<{ name: string; alias: string }>;
    expect(fields[0].alias).toBe("Object ID");
    expect(fields[1].alias).toBe("Park Name");
  });

  it("uses field name as alias when alias is not provided", () => {
    const featureResult = buildFeatureResult({
      objectIdFieldName: "OBJECTID",
      fields: [buildField("OBJECTID", 6)],
      features: [],
    });
    const pbf = buildFeatureCollectionPBuffer("1.0", featureResult);
    const result = decodePbfQueryResponse(toBuffer(pbf));

    const fields = result.fields as Array<{ name: string; alias: string }>;
    expect(fields[0].alias).toBe("OBJECTID");
  });

  it("returns empty object for empty buffer", () => {
    const result = decodePbfQueryResponse(new Uint8Array(0));
    expect(result).toEqual({});
  });
});
