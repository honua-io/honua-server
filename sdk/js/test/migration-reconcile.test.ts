import { describe, expect, it } from "vitest";

import { runLayerReconciliation, summarizeLayerReconciliation } from "../src/migration/reconcile.js";

describe("runLayerReconciliation", () => {
  it("reports pass when counts, geometries, and source keys align", async () => {
    const fetchFn = createMockFetch({
      sourceCount: 2,
      targetCount: 2,
      sourceFeatures: [
        { attributes: { OBJECTID: 1, NAME: "A" }, geometry: { x: -157.8, y: 21.3 } },
        { attributes: { OBJECTID: 2, NAME: "B" }, geometry: { x: -157.7, y: 21.2 } },
      ],
      targetFeatures: [
        { attributes: { OBJECTID: 1, NAME: "A", EXTRA: "ok" }, geometry: { x: -157.8, y: 21.3 } },
        { attributes: { OBJECTID: 2, NAME: "B", EXTRA: "ok" }, geometry: { x: -157.7, y: 21.2 } },
      ],
    });

    const report = await runLayerReconciliation({
      sourceBaseUrl: "https://source.example",
      sourceServiceId: "parcels",
      targetBaseUrl: "https://target.example",
      targetServiceId: "parcels",
      layerId: 0,
      sampleSize: 10,
      fetchFn,
    });

    expect(report.passed).toBe(true);
    expect(report.sourceFeatureCount).toBe(2);
    expect(report.targetFeatureCount).toBe(2);
    expect(report.countDelta).toBe(0);
    expect(report.targetGeometryValidityRatio).toBe(1);
    expect(report.missingInTargetAttributeKeys).toEqual([]);
    expect(report.extraInTargetAttributeKeys).toContain("EXTRA");
    expect(report.checks.every((check) => check.passed)).toBe(true);
    expect(summarizeLayerReconciliation(report)).toContain("passed=yes");
  });

  it("reports failures when target reconciliation checks do not match", async () => {
    const fetchFn = createMockFetch({
      sourceCount: 3,
      targetCount: 2,
      sourceFeatures: [
        { attributes: { OBJECTID: 1, NAME: "A", TYPE: "x" }, geometry: { x: -157.8, y: 21.3 } },
        { attributes: { OBJECTID: 2, NAME: "B", TYPE: "y" }, geometry: { x: -157.7, y: 21.2 } },
      ],
      targetFeatures: [
        { attributes: { OBJECTID: 1 }, geometry: { x: -157.8, y: 21.3 } },
        { attributes: { OBJECTID: 2 }, geometry: {} },
      ],
    });

    const report = await runLayerReconciliation({
      sourceBaseUrl: "https://source.example",
      sourceServiceId: "parcels",
      targetBaseUrl: "https://target.example",
      targetServiceId: "parcels",
      layerId: 0,
      sampleSize: 10,
      fetchFn,
    });

    expect(report.passed).toBe(false);
    expect(report.sourceFeatureCount).toBe(3);
    expect(report.targetFeatureCount).toBe(2);
    expect(report.countDelta).toBe(-1);
    expect(report.targetGeometryValidityRatio).toBe(0.5);
    expect(report.missingInTargetAttributeKeys).toEqual(["NAME", "TYPE"]);
    expect(report.checks.find((check) => check.check === "feature-count")?.passed).toBe(false);
    expect(report.checks.find((check) => check.check === "geometry-validity")?.passed).toBe(false);
    expect(report.checks.find((check) => check.check === "attribute-keys")?.passed).toBe(false);
    expect(summarizeLayerReconciliation(report)).toContain("passed=no");
  });

  it("issues reconciliation requests with connection-close headers", async () => {
    const seenHeaders: Array<HeadersInit | undefined> = [];
    const fetchFn: typeof fetch = async (_input, init) => {
      seenHeaders.push(init?.headers);
      return new Response(JSON.stringify({ features: [], count: 0 }), { status: 200 });
    };

    await runLayerReconciliation({
      sourceBaseUrl: "https://source.example",
      sourceServiceId: "parcels",
      targetBaseUrl: "https://target.example",
      targetServiceId: "parcels",
      layerId: 0,
      sampleSize: 5,
      fetchFn,
    });

    expect(seenHeaders).toHaveLength(4);
    for (const headers of seenHeaders) {
      expect(readHeader(headers, "Accept")).toBe("application/json");
      expect(readHeader(headers, "Connection")).toBe("close");
    }
  });
});

function createMockFetch(args: {
  sourceCount: number;
  targetCount: number;
  sourceFeatures: unknown[];
  targetFeatures: unknown[];
}): typeof fetch {
  return async (input) => {
    const url = String(input);
    const isSource = url.includes("source.example");
    const isCountRequest = url.includes("returnCountOnly=true");

    const payload = isCountRequest
      ? { count: isSource ? args.sourceCount : args.targetCount }
      : { features: isSource ? args.sourceFeatures : args.targetFeatures };

    return new Response(JSON.stringify(payload), { status: 200 });
  };
}

function readHeader(headers: HeadersInit | undefined, key: string): string | undefined {
  if (!headers) {
    return undefined;
  }

  const lowerKey = key.toLowerCase();
  if (headers instanceof Headers) {
    return headers.get(key) ?? headers.get(lowerKey) ?? undefined;
  }

  if (Array.isArray(headers)) {
    const hit = headers.find(([name]) => name.toLowerCase() === lowerKey);
    return hit?.[1];
  }

  const map = headers as Record<string, string>;
  for (const [name, value] of Object.entries(map)) {
    if (name.toLowerCase() === lowerKey) {
      return value;
    }
  }
  return undefined;
}
