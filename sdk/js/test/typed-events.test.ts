import { describe, expect, it } from "vitest";

import type { CompatEventPayloads } from "../src/index.js";
import { CompatEventBus, FeatureLayerCompat, GraphicCompat, HonuaClient } from "../src/index.js";

describe("Typed event emitter (Direction 20)", () => {
  describe("CompatEventBus typed on()", () => {
    it("accepts typed event listeners for known events", () => {
      const bus = new CompatEventBus();
      let receivedVisible: boolean | undefined;

      const sub = bus.on("layer.visibility-changed", (event) => {
        receivedVisible = event.payload.visible;
      });

      bus.emit("layer.visibility-changed", {
        layerId: "layer-0",
        visible: true,
      });

      expect(receivedVisible).toBe(true);
      sub.remove();
    });

    it("accepts typed listener for feature-layer.loaded", () => {
      const bus = new CompatEventBus();
      let receivedId: string | undefined;

      bus.on("feature-layer.loaded", (event) => {
        receivedId = event.payload.id;
      });

      bus.emit("feature-layer.loaded", {
        serviceId: "svc",
        layerId: 0,
        id: "svc-0",
      });

      expect(receivedId).toBe("svc-0");
    });

    it("still accepts untyped string event names", () => {
      const bus = new CompatEventBus();
      let received = false;

      bus.on("custom.event", () => {
        received = true;
      });

      bus.emit("custom.event", { data: 42 });
      expect(received).toBe(true);
    });
  });

  describe("CompatEventPayloads type-level checks", () => {
    it("type-checks known event payload shapes", () => {
      // Compile-time check — if this compiles, the types are correct
      const payloads: Partial<{
        [K in keyof CompatEventPayloads]: CompatEventPayloads[K];
      }> = {
        "layer.visibility-changed": { layerId: "l0", visible: true },
        "layer.opacity-changed": { layerId: "l0", opacity: 0.5 },
        "feature-layer.loading": { serviceId: "svc", layerId: 0, id: "svc-0" },
        "feature-layer.failed": { serviceId: "svc", layerId: 0, id: "svc-0", error: new Error("fail") },
      };
      expect(Object.keys(payloads).length).toBeGreaterThan(0);
    });
  });

  describe("FeatureLayerCompat typed watch()", () => {
    it("narrow visible watcher receives boolean", async () => {
      let receivedVisible: boolean | undefined;

      const layer = new FeatureLayerCompat({
        url: "https://example.test/rest/services/svc/FeatureServer/0",
        client: new HonuaClient({
          baseUrl: "https://example.test",
          fetchFn: async () => new Response("{}"),
        }),
      });

      layer.watch("visible", (value) => {
        receivedVisible = value;
      });

      layer.setVisibility(false);
      expect(receivedVisible).toBe(false);
    });

    it("narrow opacity watcher receives number", () => {
      let receivedOpacity: number | undefined;

      const layer = new FeatureLayerCompat({
        url: "https://example.test/rest/services/svc/FeatureServer/0",
        client: new HonuaClient({
          baseUrl: "https://example.test",
          fetchFn: async () => new Response("{}"),
        }),
      });

      layer.watch("opacity", (value) => {
        receivedOpacity = value;
      });

      layer.setOpacity(0.5);
      expect(receivedOpacity).toBe(0.5);
    });

    it("narrow loadStatus watcher receives status string", () => {
      let receivedStatus: string | undefined;

      const layer = new FeatureLayerCompat({
        url: "https://example.test/rest/services/svc/FeatureServer/0",
        client: new HonuaClient({
          baseUrl: "https://example.test",
          fetchFn: async () => new Response(JSON.stringify({ id: 0, name: "L" })),
        }),
      });

      layer.watch("loadStatus", (value) => {
        receivedStatus = value;
      });

      // Trigger load to fire loadStatus watchers
      layer.setVisibility(true); // Does not trigger loadStatus
      layer.refresh(); // Triggers loadStatus -> "not-loaded"
      expect(receivedStatus).toBe("not-loaded");
    });
  });

  describe("GraphicCompat typed watch()", () => {
    it("narrow geometry watcher receives typed geometry", () => {
      let receivedGeometry: Record<string, unknown> | null | undefined;

      const graphic = new GraphicCompat();
      graphic.watch("geometry", (value) => {
        receivedGeometry = value;
      });

      graphic.setGeometry({ x: 10, y: 20 });
      expect(receivedGeometry).toEqual({ x: 10, y: 20 });
    });

    it("narrow symbol watcher receives typed symbol", () => {
      let receivedSymbol: Record<string, unknown> | null | undefined;

      const graphic = new GraphicCompat();
      graphic.watch("symbol", (value) => {
        receivedSymbol = value;
      });

      graphic.setSymbol({ type: "simple-marker", color: [255, 0, 0] });
      expect(receivedSymbol).toEqual({ type: "simple-marker", color: [255, 0, 0] });
    });

    it("narrow loaded watcher receives boolean", async () => {
      let receivedLoaded: boolean | undefined;

      const graphic = new GraphicCompat();
      graphic.watch("loaded", (value) => {
        receivedLoaded = value;
      });

      await graphic.load();
      expect(receivedLoaded).toBe(true);
    });

    it("untyped property falls back to unknown", () => {
      let receivedValue: unknown;

      const graphic = new GraphicCompat();
      graphic.watch("customProp", (value) => {
        receivedValue = value;
      });

      // No watcher fires since customProp is never set via a setter
      expect(receivedValue).toBeUndefined();
    });
  });
});
