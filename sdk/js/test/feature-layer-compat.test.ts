import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureLayerCompat, parseFeatureLayerUrl } from "../src/index.js";

describe("parseFeatureLayerUrl", () => {
  it("parses canonical feature layer URL", () => {
    const parsed = parseFeatureLayerUrl(
      "https://example.test/rest/services/transport/FeatureServer/3",
    );
    expect(parsed.baseUrl).toBe("https://example.test");
    expect(parsed.serviceId).toBe("transport");
    expect(parsed.layerId).toBe(3);
  });

  it("parses URL with path prefix", () => {
    const parsed = parseFeatureLayerUrl(
      "https://example.test/honua/rest/services/transport/FeatureServer/8",
    );
    expect(parsed.baseUrl).toBe("https://example.test/honua");
    expect(parsed.serviceId).toBe("transport");
    expect(parsed.layerId).toBe(8);
  });

  it("parses relative URL shape", () => {
    const parsed = parseFeatureLayerUrl("/rest/services/transport/FeatureServer/8");
    expect(parsed.baseUrl).toBe("");
    expect(parsed.serviceId).toBe("transport");
    expect(parsed.layerId).toBe(8);
  });

  it("parses relative URL with path prefix", () => {
    const parsed = parseFeatureLayerUrl("/honua/rest/services/transport/FeatureServer/8");
    expect(parsed.baseUrl).toBe("/honua");
    expect(parsed.serviceId).toBe("transport");
    expect(parsed.layerId).toBe(8);
  });

  it("throws on invalid URL shape", () => {
    expect(() =>
      parseFeatureLayerUrl("https://example.test/rest/services/transport/MapServer"),
    ).toThrow();
  });
});

describe("FeatureLayerCompat", () => {
  it("maps ArcGIS-style query to Honua query endpoint", async () => {
    let requestedUrl: string | undefined;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({ id: 1000 });
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          requestedUrl = JSON.stringify(request);
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const result = await layer.queryFeatures({
      where: "1=1",
      outFields: ["objectid", "name"],
      returnGeometry: true,
    });

    expect(result).toEqual({ features: [] });
    expect(requestedUrl).toContain("\"serviceId\":\"default\"");
    expect(requestedUrl).toContain("\"layerId\":1000");
    expect(requestedUrl).toContain("\"where\":\"1=1\"");
  });

  it("supports relative service URLs with default client requests", async () => {
    const requestedUrls: string[] = [];
    const originalFetch = globalThis.fetch;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      requestedUrls.push(String(input));
      return new Response(JSON.stringify({ features: [] }), { status: 200 });
    }) as typeof fetch;

    try {
      const layer = new FeatureLayerCompat({
        url: "/rest/services/default/FeatureServer/2",
      });

      await layer.queryFeatures({
        where: "1=1",
        outFields: ["*"],
        returnGeometry: false,
      });
    } finally {
      globalThis.fetch = originalFetch;
    }

    expect(requestedUrls[0]).toContain("/rest/services/default/FeatureServer/2/query?");
    expect(requestedUrls[0]).not.toContain("honua.invalid");
  });

  it("supports prefixed relative service URLs with default client requests", async () => {
    const requestedUrls: string[] = [];
    const originalFetch = globalThis.fetch;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      requestedUrls.push(String(input));
      return new Response(JSON.stringify({ features: [] }), { status: 200 });
    }) as typeof fetch;

    try {
      const layer = new FeatureLayerCompat({
        url: "/honua/rest/services/default/FeatureServer/2",
      });

      await layer.queryFeatures({
        where: "1=1",
        outFields: ["*"],
        returnGeometry: false,
      });
    } finally {
      globalThis.fetch = originalFetch;
    }

    expect(requestedUrls[0]).toContain("/honua/rest/services/default/FeatureServer/2/query?");
    expect(requestedUrls[0]).not.toContain("honua.invalid");
  });

  it("supports load/when lifecycle helpers", async () => {
    let metadataCalls = 0;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          metadataCalls += 1;
          return Promise.resolve({ id: 1000, name: "Sample Layer" });
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    expect(layer.loaded).toBe(false);

    let callbackLayer: FeatureLayerCompat | undefined;
    const resolved = await layer.when((resolvedLayer) => {
      callbackLayer = resolvedLayer;
    });

    expect(layer.loaded).toBe(true);
    expect(callbackLayer).toBe(layer);
    expect(resolved).toBe(layer);
    expect(layer.metadata).toEqual({ id: 1000, name: "Sample Layer" });
    expect(metadataCalls).toBe(1);

    await layer.load();
    expect(metadataCalls).toBe(1);

    layer.refresh();
    expect(layer.loaded).toBe(false);
    expect(layer.metadata).toBeUndefined();

    await layer.load();
    expect(metadataCalls).toBe(2);
  });

  it("sets failed loadStatus when metadata loading fails", async () => {
    const errors: unknown[] = [];
    const eventBus = new CompatEventBus();
    eventBus.on("feature-layer.failed", (event) => {
      errors.push(event.payload);
    });

    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/7",
      eventBus,
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.reject(new Error("boom"));
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    await expect(layer.load()).rejects.toThrow("boom");
    expect(layer.loaded).toBe(false);
    expect(layer.metadata).toBeUndefined();
    expect(layer.loadStatus).toBe("failed");
    expect(errors).toHaveLength(1);
  });

  it("exposes metadata field helpers for schema lookup", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({
            id: 1000,
            fields: [
              { name: "OBJECTID", type: "esriFieldTypeOID" },
              { name: "Name", type: "esriFieldTypeString" },
            ],
          });
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    await layer.load();

    expect(layer.hasField("OBJECTID")).toBe(true);
    expect(layer.hasField("Name")).toBe(true);
    expect(layer.hasField("objectid")).toBe(false);
    expect(layer.hasField("NAME")).toBe(false);
    expect(layer.hasField("missing")).toBe(false);
    expect(layer.getField("Name")).toEqual({
      name: "Name",
      type: "esriFieldTypeString",
    });
    expect(layer.getField("name")).toBeUndefined();
    expect(layer.getField("")).toBeUndefined();

    const fields = layer.listFields();
    expect(fields).toEqual([
      { name: "OBJECTID", type: "esriFieldTypeOID" },
      { name: "Name", type: "esriFieldTypeString" },
    ]);
    (fields as Array<Record<string, unknown>>).push({ name: "MUTATED" });
    expect(layer.listFields()).toHaveLength(2);
  });

  it("supports watch handles for lifecycle and property mutations", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/0",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({ id: 0, name: "Hydrants" });
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const loadStatusValues: unknown[] = [];
    const visibleValues: unknown[] = [];
    const opacityValues: unknown[] = [];
    const metadataValues: unknown[] = [];

    const loadStatusHandle = layer.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const visibleHandle = layer.watch("visible", (value) => {
      visibleValues.push(value);
    });
    const opacityHandle = layer.watch("opacity", (value) => {
      opacityValues.push(value);
    });
    const metadataHandle = layer.watch("metadata", (value) => {
      metadataValues.push(value);
    });

    await layer.load();
    layer.setVisibility(false);
    layer.setOpacity(0.4);
    layer.refresh();

    loadStatusHandle.remove();
    visibleHandle.remove();
    opacityHandle.remove();
    metadataHandle.remove();

    layer.setVisibility(true);
    layer.setOpacity(0.9);

    expect(loadStatusValues).toEqual(["loading", "loaded", "not-loaded"]);
    expect(visibleValues).toEqual([false]);
    expect(opacityValues).toEqual([0.4]);
    expect(metadataValues).toEqual([{ id: 0, name: "Hydrants" }, undefined]);
    expect(layer.loadStatus).toBe("not-loaded");
    expect(layer.loaded).toBe(false);
    expect(layer.visible).toBe(true);
    expect(layer.opacity).toBe(0.9);
  });

  it("creates a default query object", () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    expect(layer.createQuery()).toEqual({
      where: "1=1",
      outFields: ["*"],
      returnGeometry: true,
    });
  });

  it("uses constructor defaults for outFields and definitionExpression", async () => {
    const capturedQueries: string[] = [];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      outFields: ["OBJECTID", "NAME"],
      definitionExpression: "status = 1",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          capturedQueries.push(JSON.stringify(request));
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    expect(layer.createQuery()).toEqual({
      where: "status = 1",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: true,
    });

    await layer.queryFeatures();
    await layer.queryObjectIds();
    await layer.queryFeatureCount();

    expect(capturedQueries[0]).toContain('"where":"status = 1"');
    expect(capturedQueries[0]).toContain('"outFields":["OBJECTID","NAME"]');
    expect(capturedQueries[1]).toContain('"where":"status = 1"');
    expect(capturedQueries[2]).toContain('"where":"status = 1"');
  });

  it("supports queryObjectIds with returnIdsOnly passthrough", async () => {
    let lastQuery: unknown;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          lastQuery = request;
          return Promise.resolve({ objectIds: [7, "8", "NaN"] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const objectIds = await layer.queryObjectIds({ where: "status = 'open'" });

    expect(objectIds).toEqual([7, 8]);
    expect(JSON.stringify(lastQuery)).toContain('"where":"status = \'open\'"');
    expect(JSON.stringify(lastQuery)).toContain('"returnIdsOnly":true');
  });

  it("supports queryObjectIds fallback parsing from feature attributes", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({
            features: [
              { attributes: { OBJECTID: 11 } },
              { attributes: { objectid: "12" } },
              { attributes: { id: 13 } },
              { attributes: { name: "missing-object-id" } },
            ],
          });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const objectIds = await layer.queryObjectIds();
    expect(objectIds).toEqual([11, 12, 13]);
  });

  it("supports queryFeatureCount with returnCountOnly and fallback feature length", async () => {
    let firstRequest: unknown;
    let queryCalls = 0;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          queryCalls += 1;
          if (queryCalls === 1) {
            firstRequest = request;
            return Promise.resolve({ count: 42 });
          }
          return Promise.resolve({ features: [{}, {}, {}] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const count = await layer.queryFeatureCount({ where: "1=1" });
    const fallbackCount = await layer.queryFeatureCount({ where: "1=0" });

    expect(count).toBe(42);
    expect(fallbackCount).toBe(3);
    expect(JSON.stringify(firstRequest)).toContain('"returnCountOnly":true');
  });

  it("supports queryFeaturesAll pagination helper", async () => {
    const capturedQueries: Array<Record<string, unknown>> = [];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          const typed = request as Record<string, unknown>;
          capturedQueries.push(typed);
          const extraParams = (typed.extraParams ?? {}) as Record<string, unknown>;
          const offset = Number(extraParams.resultOffset ?? 0);
          if (offset === 0) {
            return Promise.resolve({ features: [{ id: 1 }, { id: 2 }] });
          }
          if (offset === 2) {
            return Promise.resolve({ features: [{ id: 3 }] });
          }
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const features = await layer.queryFeaturesAll({
      pageSize: 2,
      extraParams: {
        resultOffset: 9999,
        resultRecordCount: 1,
      },
    });

    expect(features).toEqual([{ id: 1 }, { id: 2 }, { id: 3 }]);
    expect(capturedQueries).toHaveLength(2);
    expect(capturedQueries[0]?.extraParams).toMatchObject({
      resultOffset: 0,
      resultRecordCount: 2,
    });
    expect(capturedQueries[1]?.extraParams).toMatchObject({
      resultOffset: 2,
      resultRecordCount: 2,
    });
  });

  it("supports queryExtent with returnExtentOnly passthrough", async () => {
    let lastQuery: unknown;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          lastQuery = request;
          return Promise.resolve({
            extent: {
              xmin: -158,
              ymin: 21,
              xmax: -157,
              ymax: 22,
              spatialReference: { wkid: 4326 },
            },
            count: 9,
          });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const result = await layer.queryExtent({ where: "status = 'open'" });
    expect(result).toEqual({
      extent: {
        xmin: -158,
        ymin: 21,
        xmax: -157,
        ymax: 22,
        spatialReference: { wkid: 4326 },
      },
      count: 9,
    });
    expect(JSON.stringify(lastQuery)).toContain('"where":"status = \'open\'"');
    expect(JSON.stringify(lastQuery)).toContain('"returnExtentOnly":true');
  });

  it("returns null extent when queryExtent response shape is unknown", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [{}, {}] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    await expect(layer.queryExtent()).resolves.toEqual({ extent: null });
  });

  it("maps queryRelatedFeatures to related records request", async () => {
    let relatedRequest: unknown;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      definitionExpression: "status = 1",
      outFields: ["OBJECTID", "NAME"],
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryRelatedRecords(request: unknown): Promise<unknown> {
          relatedRequest = request;
          return Promise.resolve({ relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }] });
        }
      })() as any,
    });

    const result = await layer.queryRelatedFeatures({
      relationshipId: 4,
      objectIds: [10, 11],
      returnGeometry: false,
    });

    expect(result).toEqual({ relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }] });
    expect(JSON.stringify(relatedRequest)).toContain('"serviceId":"default"');
    expect(JSON.stringify(relatedRequest)).toContain('"layerId":1000');
    expect(JSON.stringify(relatedRequest)).toContain('"relationshipId":4');
    expect(JSON.stringify(relatedRequest)).toContain('"objectIds":[10,11]');
    expect(JSON.stringify(relatedRequest)).toContain('"where":"status = 1"');
    expect(JSON.stringify(relatedRequest)).toContain('"outFields":["OBJECTID","NAME"]');
    expect(JSON.stringify(relatedRequest)).toContain('"returnGeometry":false');
  });

  it("maps queryRelatedRecords alias to related records request", async () => {
    let relatedRequest: unknown;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      definitionExpression: "status = 1",
      outFields: ["OBJECTID", "NAME"],
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryRelatedRecords(request: unknown): Promise<unknown> {
          relatedRequest = request;
          return Promise.resolve({ relatedRecordGroups: [{ objectId: 2, relatedRecords: [] }] });
        }
      })() as any,
    });

    const result = await layer.queryRelatedRecords({
      relationshipId: 5,
      objectIds: [20, 21],
      returnGeometry: false,
    });

    expect(result).toEqual({ relatedRecordGroups: [{ objectId: 2, relatedRecords: [] }] });
    expect(JSON.stringify(relatedRequest)).toContain('"serviceId":"default"');
    expect(JSON.stringify(relatedRequest)).toContain('"layerId":1000');
    expect(JSON.stringify(relatedRequest)).toContain('"relationshipId":5');
    expect(JSON.stringify(relatedRequest)).toContain('"objectIds":[20,21]');
    expect(JSON.stringify(relatedRequest)).toContain('"where":"status = 1"');
    expect(JSON.stringify(relatedRequest)).toContain('"outFields":["OBJECTID","NAME"]');
    expect(JSON.stringify(relatedRequest)).toContain('"returnGeometry":false');
  });

  it("maps attachment helper calls to attachment endpoints", async () => {
    const requests: Array<{
      method?: string;
      path?: string;
      query?: Record<string, unknown>;
      body?: unknown;
    }> = [];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryRelatedRecords(): Promise<unknown> {
          return Promise.resolve({});
        }

        public request(request: unknown): Promise<unknown> {
          requests.push(request as any);
          return Promise.resolve({ ok: true });
        }
      })() as any,
    });

    await layer.queryAttachments({ objectIds: [1, 2], where: "1=1" });
    await layer.listAttachments({ objectId: 99 });
    await layer.deleteAttachments({ objectId: 99, attachmentIds: [7, 8] });
    await layer.addAttachment({
      objectId: 99,
      attachment: "hello-world",
      name: "notes.txt",
      contentType: "text/plain",
      extraParams: { token: "abc" },
    });
    await layer.updateAttachment({
      objectId: 99,
      attachmentId: 7,
      attachment: new Uint8Array([1, 2, 3]),
      name: "capture.bin",
    });

    expect(requests).toHaveLength(5);
    expect(requests[0]?.path).toContain("/FeatureServer/1000/queryAttachments");
    expect(requests[0]?.query).toMatchObject({ objectIds: "1,2", where: "1=1" });
    expect(requests[1]?.path).toContain("/FeatureServer/1000/99/attachments");
    expect(requests[2]?.path).toContain("/FeatureServer/1000/99/deleteAttachments");
    expect(String(requests[2]?.body)).toContain("attachmentIds=7%2C8");

    expect(requests[3]?.method).toBe("POST");
    expect(requests[3]?.path).toContain("/FeatureServer/1000/99/addAttachment");
    expect(requests[3]?.query).toMatchObject({ token: "abc" });
    expect(requests[3]?.body).toBeInstanceOf(FormData);
    const addAttachmentBody = requests[3]?.body as FormData;
    expect(addAttachmentBody.get("name")).toBe("notes.txt");
    expect(addAttachmentBody.get("attachment")).toBeInstanceOf(Blob);

    expect(requests[4]?.method).toBe("POST");
    expect(requests[4]?.path).toContain("/FeatureServer/1000/99/updateAttachment");
    expect(requests[4]?.body).toBeInstanceOf(FormData);
    const updateAttachmentBody = requests[4]?.body as FormData;
    expect(updateAttachmentBody.get("attachmentId")).toBe("7");
    expect(updateAttachmentBody.get("name")).toBe("capture.bin");
    expect(updateAttachmentBody.get("attachment")).toBeInstanceOf(Blob);
  });

  it("rejects attachment payloads larger than maxAttachmentBytes", () => {
    const requests: unknown[] = [];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      maxAttachmentBytes: 4,
      client: new (class {
        public request(request: unknown): Promise<unknown> {
          requests.push(request);
          return Promise.resolve({ ok: true });
        }
      })() as any,
    });

    expect(() =>
      layer.addAttachment({
        objectId: 99,
        attachment: "hello-world",
      }),
    ).toThrow("exceeds maxAttachmentBytes");
    expect(requests).toHaveLength(0);

    expect(() =>
      layer.updateAttachment({
        objectId: 99,
        attachmentId: 7,
        attachment: new Uint8Array([1, 2, 3, 4, 5]),
        maxAttachmentBytes: 4,
      }),
    ).toThrow("exceeds maxAttachmentBytes");
    expect(requests).toHaveLength(0);
  });

  it("preserves common display options and emits event bus lifecycle changes", async () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    const capturedQueries: unknown[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const renderer = { type: "simple" };
    const popupTemplate = { title: "{NAME}" };
    const initialLabeling = [{ where: "1=1" }];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      id: "parcels",
      title: "Parcels",
      renderer,
      popupTemplate,
      labelingInfo: initialLabeling,
      labelsVisible: false,
      opacity: 0.6,
      visible: false,
      minScale: 12000,
      maxScale: 2400,
      legendEnabled: false,
      listMode: "hide",
      eventBus,
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({ id: 1000 });
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          capturedQueries.push(request);
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    expect(layer.id).toBe("parcels");
    expect(layer.title).toBe("Parcels");
    expect(layer.renderer).toEqual(renderer);
    expect(layer.popupTemplate).toEqual(popupTemplate);
    expect(layer.labelingInfo).toEqual(initialLabeling);
    expect(layer.labelsVisible).toBe(false);
    expect(layer.opacity).toBe(0.6);
    expect(layer.visible).toBe(false);
    expect(layer.minScale).toBe(12000);
    expect(layer.maxScale).toBe(2400);
    expect(layer.legendEnabled).toBe(false);
    expect(layer.listMode).toBe("hide");
    expect(layer.eventBus).toBe(eventBus);

    await layer.load();
    layer.setVisibility(true);
    layer.setOpacity(2);
    layer.setRenderer({ type: "class-breaks" });
    layer.setPopupTemplate({ title: "Updated" });
    layer.setLabelingInfo([{ where: "status = 'open'" }]);
    layer.setDefinitionExpression("status = 5");
    layer.setOutFields(["OBJECTID"]);
    layer.setLabelsVisible(true);
    layer.setScaleRange(6000, 0);
    layer.setLegendEnabled(true);
    await layer.queryFeatures();
    layer.refresh();

    expect(layer.visible).toBe(true);
    expect(layer.opacity).toBe(1);
    expect(layer.renderer).toEqual({ type: "class-breaks" });
    expect(layer.popupTemplate).toEqual({ title: "Updated" });
    expect(layer.labelingInfo).toEqual([{ where: "status = 'open'" }]);
    expect(layer.definitionExpression).toBe("status = 5");
    expect(layer.outFields).toEqual(["OBJECTID"]);
    expect(layer.labelsVisible).toBe(true);
    expect(layer.minScale).toBe(6000);
    expect(layer.maxScale).toBe(0);
    expect(layer.legendEnabled).toBe(true);
    expect(JSON.stringify(capturedQueries[0])).toContain('"where":"status = 5"');
    expect(JSON.stringify(capturedQueries[0])).toContain('"outFields":["OBJECTID"]');
    expect(events).toContain("feature-layer.loaded");
    expect(events).toContain("layer.visibility-changed");
    expect(events).toContain("layer.opacity-changed");
    expect(events).toContain("feature-layer.renderer-changed");
    expect(events).toContain("feature-layer.popup-template-changed");
    expect(events).toContain("feature-layer.labeling-changed");
    expect(events).toContain("feature-layer.definition-expression-changed");
    expect(events).toContain("feature-layer.out-fields-changed");
    expect(events).toContain("feature-layer.labels-visible-changed");
    expect(events).toContain("feature-layer.scale-range-changed");
    expect(events).toContain("feature-layer.legend-enabled-changed");
    expect(events).toContain("feature-layer.refreshed");
  });

  it("queryFeaturesStream yields pages via compat layer", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
      client: {
        queryFeatures: async (request: Record<string, unknown>) => {
          const offset = (request.extraParams as Record<string, number>)?.resultOffset ?? 0;
          if (offset === 0) {
            return { features: [{ attributes: { OBJECTID: 1 } }, { attributes: { OBJECTID: 2 } }] };
          }
          return { features: [{ attributes: { OBJECTID: 3 } }] };
        },
      } as never,
    });

    const pages: unknown[][] = [];
    for await (const page of layer.queryFeaturesStream({ pageSize: 2 })) {
      pages.push(page);
    }

    expect(pages).toEqual([
      [{ attributes: { OBJECTID: 1 } }, { attributes: { OBJECTID: 2 } }],
      [{ attributes: { OBJECTID: 3 } }],
    ]);
  });

  it(".on('edits') fires after applyEdits with result payload", async () => {
    const editResult = { addResults: [{ objectId: 1, success: true }] };
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
      client: {
        applyEdits: async () => editResult,
      } as never,
    });

    const received: unknown[] = [];
    layer.on("edits", (event) => {
      received.push(event);
    });

    await layer.applyEdits({ adds: [{ attributes: { name: "Test" } }] });

    expect(received).toHaveLength(1);
    expect(received[0]).toMatchObject({ result: editResult });
  });

  it(".on() returns a handle with .remove() that stops listener", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
      client: {
        applyEdits: async () => ({ success: true }),
      } as never,
    });

    const received: unknown[] = [];
    const handle = layer.on("edits", (event) => {
      received.push(event);
    });

    await layer.applyEdits({ adds: [] });
    handle.remove();
    await layer.applyEdits({ adds: [] });

    expect(received).toHaveLength(1);
  });

  it(".on() with unsupported event name does not throw", () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
    });

    expect(() => layer.on("unsupported-event", () => {})).not.toThrow();
  });

  it("multiple .on() listeners for same event all fire", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
      client: {
        applyEdits: async () => ({ ok: true }),
      } as never,
    });

    const firstReceived: unknown[] = [];
    const secondReceived: unknown[] = [];
    layer.on("edits", (event) => firstReceived.push(event));
    layer.on("edits", (event) => secondReceived.push(event));

    await layer.applyEdits({ adds: [] });

    expect(firstReceived).toHaveLength(1);
    expect(secondReceived).toHaveLength(1);
  });

  it("setTimeExtent stores extent and emits event", () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => events.push(event.type));

    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
      eventBus,
    });

    const start = new Date("2024-01-01");
    const end = new Date("2024-06-01");
    layer.setTimeExtent({ start, end });

    expect(layer.timeExtent).toBeDefined();
    expect(layer.timeExtent!.start.getTime()).toBe(start.getTime());
    expect(layer.timeExtent!.end.getTime()).toBe(end.getTime());
    expect(events).toContain("feature-layer.time-extent-change");
  });

  it("queryFeatures includes time param when timeExtent is set", async () => {
    const capturedParams: Record<string, unknown>[] = [];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
      client: {
        queryFeatures: async (request: Record<string, unknown>) => {
          capturedParams.push(request);
          return { features: [] };
        },
      } as never,
    });

    const start = new Date("2024-01-01");
    const end = new Date("2024-06-01");
    layer.setTimeExtent({ start, end });

    await layer.queryFeatures({ where: "1=1" });

    const extraParams = capturedParams[0]?.extraParams as Record<string, string>;
    expect(extraParams?.time).toBe(`${start.getTime()},${end.getTime()}`);
  });

  it("queryFeatures does NOT include time when timeExtent is unset", async () => {
    const capturedParams: Record<string, unknown>[] = [];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
      client: {
        queryFeatures: async (request: Record<string, unknown>) => {
          capturedParams.push(request);
          return { features: [] };
        },
      } as never,
    });

    await layer.queryFeatures({ where: "1=1" });

    expect(capturedParams[0]?.extraParams).toBeUndefined();
  });

  it("queryFeatures does NOT override explicit time param in extraParams", async () => {
    const capturedParams: Record<string, unknown>[] = [];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/transport/FeatureServer/3",
      client: {
        queryFeatures: async (request: Record<string, unknown>) => {
          capturedParams.push(request);
          return { features: [] };
        },
      } as never,
    });

    layer.setTimeExtent({ start: new Date("2024-01-01"), end: new Date("2024-06-01") });

    await layer.queryFeatures({
      where: "1=1",
      extraParams: { time: "custom-value" },
    });

    const extraParams = capturedParams[0]?.extraParams as Record<string, string>;
    expect(extraParams?.time).toBe("custom-value");
  });

  it("destroy() clears watchers, event listeners, and emits feature-layer.destroyed", () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/svc/FeatureServer/0",
      eventBus,
    });

    const watchValues: unknown[] = [];
    layer.watch("visible", (v) => watchValues.push(v));

    const eventPayloads: unknown[] = [];
    layer.on("edits", (e) => eventPayloads.push(e));

    layer.destroy();

    expect(eventTypes).toContain("feature-layer.destroyed");

    layer.setVisibility(false);
    expect(watchValues).toHaveLength(0);
  });
});
