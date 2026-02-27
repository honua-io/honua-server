import { describe, expect, it } from "vitest";

import { CompatEventBus, MapCompat, MapViewCompat, SearchCompat } from "../src/index.js";

describe("SearchCompat", () => {
  it("supports when() and watch() for load and search state changes", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const source = {
      search: async ({ searchTerm }: { searchTerm: string }) => [
        {
          name: `Result: ${searchTerm}`,
          location: { x: -157.8583, y: 21.3069 },
        },
      ],
    };

    const search = new SearchCompat({
      eventBus,
      sources: [source],
      includeDefaultSources: false,
      autoNavigate: false,
    });

    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const sourceValues: unknown[] = [];
    const searchTermValues: unknown[] = [];
    const selectedResultIndexValues: unknown[] = [];

    const loadStatusHandle = search.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = search.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const sourceHandle = search.watch("sources", (value) => {
      sourceValues.push(value);
    });
    const searchTermHandle = search.watch("searchTerm", (value) => {
      searchTermValues.push(value);
    });
    const selectedResultIndexHandle = search.watch("selectedResultIndex", (value) => {
      selectedResultIndexValues.push(value);
    });

    let callbackWidget: SearchCompat | undefined;
    const widget = await search.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });

    await search.search("honolulu");
    search.clear();

    loadStatusHandle.remove();
    loadedHandle.remove();
    sourceHandle.remove();
    searchTermHandle.remove();
    selectedResultIndexHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      sources: sourceValues.length,
      searchTerm: searchTermValues.length,
      selectedResultIndex: selectedResultIndexValues.length,
    };
    await search.search("waikiki");

    expect(widget).toBe(search);
    expect(callbackWidget).toBe(search);
    expect(search.loaded).toBe(true);
    expect(search.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(sourceValues).toHaveLength(1);
    expect(Array.isArray(sourceValues[0])).toBe(true);
    expect((sourceValues[0] as unknown[]).length).toBe(1);
    expect(searchTermValues).toEqual(["honolulu", ""]);
    expect(selectedResultIndexValues).toEqual([0, -1]);
    expect(seenTypes).toContain("search.loading");
    expect(seenTypes).toContain("search.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(sourceValues).toHaveLength(watchSnapshot.sources);
    expect(searchTermValues).toHaveLength(watchSnapshot.searchTerm);
    expect(selectedResultIndexValues).toHaveLength(watchSnapshot.selectedResultIndex);
  });

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
    const sourceTwo = {
      search: async ({ searchTerm }: { searchTerm: string }) => [
        {
          name: `Other: ${searchTerm}`,
          location: { x: -157.95, y: 21.31 },
        },
      ],
    };

    const search = new SearchCompat({
      view,
      eventBus,
      sources: [source],
      includeDefaultSources: false,
    });
    search.addSource(sourceTwo);
    expect(search.sources).toEqual([source, sourceTwo]);
    expect(search.removeSource(sourceTwo)).toBe(true);
    expect(search.sources).toEqual([source]);
    search.setSources([source, sourceTwo], { includeDefaultSources: false });
    expect(search.sources).toEqual([source, sourceTwo]);

    const response = await search.search("honolulu");
    expect(response.searchTerm).toBe("honolulu");
    expect(response.results).toHaveLength(2);
    expect(response.results[0]).toMatchObject({ name: "Result: honolulu" });
    expect(response.results[1]).toMatchObject({ name: "Other: honolulu" });
    expect(search.selectedResult).toMatchObject({ name: "Result: honolulu" });
    expect(search.selectedResultIndex).toBe(0);
    expect(goToTargets).toEqual([{ x: -157.8583, y: 21.3069 }]);

    expect(await search.selectResult(1)).toMatchObject({ name: "Other: honolulu" });
    expect(search.selectedResultIndex).toBe(1);
    expect(await search.previousResult()).toMatchObject({ name: "Result: honolulu" });
    expect(search.selectedResultIndex).toBe(0);
    expect(await search.nextResult()).toMatchObject({ name: "Other: honolulu" });
    expect(search.selectedResultIndex).toBe(1);
    expect(goToTargets).toEqual([
      { x: -157.8583, y: 21.3069 },
      { x: -157.95, y: 21.31 },
      { x: -157.8583, y: 21.3069 },
      { x: -157.95, y: 21.31 },
    ]);

    const suggestResponse = await search.suggest("hono");
    expect(suggestResponse.suggestions).toEqual([{ text: "hono suggestion", source }]);

    search.clear();
    expect(search.searchTerm).toBe("");
    expect(search.results).toEqual([]);
    expect(search.suggestions).toEqual([]);
    expect(search.selectedResult).toBeUndefined();
    expect(search.selectedResultIndex).toBe(-1);

    expect(events).toContain("search.started");
    expect(events).toContain("search.completed");
    expect(events).toContain("search.navigated");
    expect(events).toContain("search.suggestions-updated");
    expect(events).toContain("search.sources-changed");
    expect(events).toContain("search.result-selected");
    expect(events).toContain("search.cleared");

    search.destroy();
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
    const map = new MapCompat({ layers: [layer] });
    const view = new MapViewCompat({
      map,
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
    expect(search.selectedResultIndex).toBe(0);

    const nextLayer = {
      id: "landmarks",
      title: "Landmarks",
      queryFeatures: async () => ({
        features: [
          {
            attributes: {
              NAME: "Diamond Head",
            },
            geometry: {
              x: -157.805,
              y: 21.262,
            },
          },
        ],
      }),
    };
    map.add(nextLayer);
    expect(search.sources.length).toBeGreaterThanOrEqual(2);

    search.destroy();
  });
});
