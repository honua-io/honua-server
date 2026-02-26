import { describe, expect, it } from "vitest";

import { CompatEventBus, MapCompat, MapViewCompat, SearchCompat } from "../src/index.js";

describe("SearchCompat", () => {
  it("supports custom sources with search/suggest/clear and emits lifecycle events", async () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const goToTargets: unknown[] = [];
    const view = {
      goTo: async (target: unknown) => {
        goToTargets.push(target);
      },
    };
    const source = {
      search: async ({ searchTerm }: { searchTerm: string }) => [
        {
          name: `Result: ${searchTerm}`,
          location: { x: -157.8583, y: 21.3069 },
        },
      ],
      suggest: async ({ searchTerm }: { searchTerm: string }) => [
        {
          text: `${searchTerm} suggestion`,
        },
      ],
    };

    const search = new SearchCompat({
      view,
      eventBus,
      sources: [source],
      includeDefaultSources: false,
    });

    const response = await search.search("honolulu");
    expect(response.searchTerm).toBe("honolulu");
    expect(response.results).toHaveLength(1);
    expect(response.results[0]).toMatchObject({ name: "Result: honolulu" });
    expect(search.selectedResult).toMatchObject({ name: "Result: honolulu" });
    expect(goToTargets).toEqual([{ x: -157.8583, y: 21.3069 }]);

    const suggestResponse = await search.suggest("hono");
    expect(suggestResponse.suggestions).toEqual([{ text: "hono suggestion", source }]);

    search.clear();
    expect(search.searchTerm).toBe("");
    expect(search.results).toEqual([]);
    expect(search.suggestions).toEqual([]);
    expect(search.selectedResult).toBeUndefined();

    expect(events).toContain("search.started");
    expect(events).toContain("search.completed");
    expect(events).toContain("search.navigated");
    expect(events).toContain("search.suggestions-updated");
    expect(events).toContain("search.cleared");
  });

  it("builds default layer-backed sources from view map", async () => {
    const layer = {
      id: "places",
      title: "Places",
      queryFeatures: async () => ({
        features: [
          {
            attributes: {
              NAME: "Central Park",
              CITY: "Honolulu",
            },
            geometry: {
              x: -157.858,
              y: 21.307,
            },
          },
          {
            attributes: {
              NAME: "Beach",
            },
            geometry: {
              x: -157.9,
              y: 21.28,
            },
          },
        ],
      }),
    };
    const view = new MapViewCompat({
      map: new MapCompat({ layers: [layer] }),
      center: [-157.8, 21.3],
      zoom: 4,
    });

    const search = new SearchCompat({
      view,
    });
    const response = await search.search("park");

    expect(response.results).toHaveLength(1);
    expect(response.results[0]).toMatchObject({
      name: "Central Park",
      location: { x: -157.858, y: 21.307 },
    });
    expect(search.selectedResult).toMatchObject({
      name: "Central Park",
    });
  });
});
