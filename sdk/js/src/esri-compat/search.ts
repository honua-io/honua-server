import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface SearchCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  sources?: readonly SearchSourceCompat[];
  includeDefaultSources?: boolean;
  autoNavigate?: boolean;
  autoRefreshSources?: boolean;
}

export interface SearchRequestCompat {
  searchTerm: string;
}

export interface SearchResultCompat {
  name: string;
  feature?: unknown;
  location?: unknown;
  extent?: unknown;
  source?: unknown;
}

export interface SearchSuggestionCompat {
  text: string;
  key?: string;
  source?: unknown;
}

export interface SearchResponseCompat {
  searchTerm: string;
  results: SearchResultCompat[];
}

export interface SuggestResponseCompat {
  searchTerm: string;
  suggestions: SearchSuggestionCompat[];
}

export interface SearchSourceCompat {
  search(request: SearchRequestCompat): Promise<readonly SearchResultCompat[]> | readonly SearchResultCompat[];
  suggest?(
    request: SearchRequestCompat,
  ): Promise<readonly SearchSuggestionCompat[]> | readonly SearchSuggestionCompat[];
}

export type SearchLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface SearchHandleCompat {
  remove(): void;
}

export class SearchCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly autoNavigate: boolean;
  public includeDefaultSources: boolean;
  public readonly autoRefreshSources: boolean;
  public loaded: boolean;
  public loadStatus: SearchLoadStatusCompat;
  public sources: SearchSourceCompat[];
  public searchTerm: string;
  public results: SearchResultCompat[];
  public suggestions: SearchSuggestionCompat[];
  public selectedResult: SearchResultCompat | undefined;
  public selectedResultIndex: number;

  private readonly subscriptions: CompatEventSubscription[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private customSources: SearchSourceCompat[];

  public constructor(options: SearchCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.autoNavigate = options.autoNavigate ?? true;
    this.includeDefaultSources = options.includeDefaultSources ?? true;
    this.autoRefreshSources = options.autoRefreshSources ?? true;
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.customSources = [...(options.sources ?? [])];
    this.sources = [];
    this.searchTerm = "";
    this.results = [];
    this.suggestions = [];
    this.selectedResult = undefined;
    this.selectedResultIndex = -1;
    this.subscriptions = [];
    this.watchListeners = new Map();
    this.refreshSourceSubscriptions();
    this.rebuildSources(false);
  }

  public async load(): Promise<SearchCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("search.loading", undefined, this);
    this.rebuildSources(false);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("search.loaded", { sourceCount: this.sources.length }, this);
    return this;
  }

  public async when(callback?: (widget: SearchCompat) => void): Promise<SearchCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SearchHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public async search(termOrRequest: string | Partial<SearchRequestCompat>): Promise<SearchResponseCompat> {
    const searchTerm =
      typeof termOrRequest === "string"
        ? termOrRequest
        : typeof termOrRequest.searchTerm === "string"
          ? termOrRequest.searchTerm
          : "";
    this.searchTerm = searchTerm;
    this.notifyWatchers("searchTerm", this.searchTerm);
    this.eventBus.emit("search.started", { searchTerm }, this);

    const allResults: SearchResultCompat[] = [];
    for (const source of this.sources) {
      try {
        const sourceResults = await source.search({ searchTerm });
        for (const result of sourceResults) {
          allResults.push({
            ...result,
            source: result.source ?? source,
          });
        }
      } catch (error) {
        this.eventBus.emit("search.source-error", { searchTerm, error }, this);
      }
    }

    this.results = allResults;
    this.notifyWatchers("results", this.results);
    this.selectedResult = this.results[0];
    this.notifyWatchers("selectedResult", this.selectedResult);
    this.selectedResultIndex = this.selectedResult ? 0 : -1;
    this.notifyWatchers("selectedResultIndex", this.selectedResultIndex);
    if (this.autoNavigate) {
      await this.navigateToSelectedResult();
    }

    const response: SearchResponseCompat = {
      searchTerm,
      results: [...this.results],
    };
    this.eventBus.emit(
      "search.completed",
      {
        searchTerm,
        resultCount: this.results.length,
      },
      this,
    );
    return response;
  }

  public async suggest(termOrRequest: string | Partial<SearchRequestCompat>): Promise<SuggestResponseCompat> {
    const searchTerm =
      typeof termOrRequest === "string"
        ? termOrRequest
        : typeof termOrRequest.searchTerm === "string"
          ? termOrRequest.searchTerm
          : "";

    const suggestions: SearchSuggestionCompat[] = [];
    for (const source of this.sources) {
      if (!source.suggest) {
        continue;
      }

      try {
        const sourceSuggestions = await source.suggest({ searchTerm });
        for (const suggestion of sourceSuggestions) {
          suggestions.push({
            ...suggestion,
            source: suggestion.source ?? source,
          });
        }
      } catch (error) {
        this.eventBus.emit("search.suggest-error", { searchTerm, error }, this);
      }
    }

    this.suggestions = suggestions;
    this.notifyWatchers("suggestions", this.suggestions);
    this.eventBus.emit(
      "search.suggestions-updated",
      {
        searchTerm,
        suggestionCount: suggestions.length,
      },
      this,
    );
    return {
      searchTerm,
      suggestions: [...suggestions],
    };
  }

  public clear(): void {
    this.searchTerm = "";
    this.notifyWatchers("searchTerm", this.searchTerm);
    this.results = [];
    this.notifyWatchers("results", this.results);
    this.suggestions = [];
    this.notifyWatchers("suggestions", this.suggestions);
    this.selectedResult = undefined;
    this.notifyWatchers("selectedResult", this.selectedResult);
    this.selectedResultIndex = -1;
    this.notifyWatchers("selectedResultIndex", this.selectedResultIndex);
    this.eventBus.emit("search.cleared", undefined, this);
  }

  public setSources(
    sources: readonly SearchSourceCompat[],
    options: { includeDefaultSources?: boolean } = {},
  ): void {
    const includeDefaults = options.includeDefaultSources ?? this.includeDefaultSources;
    this.includeDefaultSources = includeDefaults;
    this.notifyWatchers("includeDefaultSources", this.includeDefaultSources);
    this.customSources = [...sources];
    this.refreshSourceSubscriptions();
    this.rebuildSources(true);
  }

  public addSource(source: SearchSourceCompat): void {
    this.customSources.push(source);
    this.rebuildSources(true);
  }

  public removeSource(source: SearchSourceCompat): boolean {
    const index = this.customSources.indexOf(source);
    if (index < 0) {
      return false;
    }

    this.customSources.splice(index, 1);
    this.rebuildSources(true);
    return true;
  }

  public async selectResult(resultOrIndex: SearchResultCompat | number): Promise<SearchResultCompat | undefined> {
    if (this.results.length === 0) {
      return undefined;
    }

    const index =
      typeof resultOrIndex === "number"
        ? normalizeResultIndex(resultOrIndex, this.results.length)
        : this.results.indexOf(resultOrIndex);
    if (index < 0) {
      return undefined;
    }

    this.selectedResultIndex = index;
    this.notifyWatchers("selectedResultIndex", this.selectedResultIndex);
    this.selectedResult = this.results[index];
    this.notifyWatchers("selectedResult", this.selectedResult);
    if (this.autoNavigate) {
      await this.navigateToSelectedResult();
    }

    this.eventBus.emit(
      "search.result-selected",
      {
        selectedResultIndex: this.selectedResultIndex,
        selectedResult: this.selectedResult,
      },
      this,
    );
    return this.selectedResult;
  }

  public async nextResult(): Promise<SearchResultCompat | undefined> {
    if (this.results.length === 0) {
      return undefined;
    }
    const current = this.selectedResultIndex >= 0 ? this.selectedResultIndex : 0;
    const next = Math.min(current + 1, this.results.length - 1);
    return this.selectResult(next);
  }

  public async previousResult(): Promise<SearchResultCompat | undefined> {
    if (this.results.length === 0) {
      return undefined;
    }
    const current = this.selectedResultIndex >= 0 ? this.selectedResultIndex : 0;
    const previous = Math.max(current - 1, 0);
    return this.selectResult(previous);
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
    this.watchListeners.clear();
  }

  private async navigateToSelectedResult(): Promise<void> {
    if (!this.selectedResult) {
      return;
    }
    if (!isGoToProvider(this.view)) {
      return;
    }

    const target = this.selectedResult.extent ?? this.selectedResult.location ?? this.selectedResult.feature;
    if (target === undefined) {
      return;
    }

    await this.view.goTo(target);
    this.eventBus.emit("search.navigated", { target }, this);
  }

  private rebuildSources(emitChange: boolean): void {
    this.sources = [
      ...this.customSources,
      ...(this.includeDefaultSources ? resolveViewSearchSources(this.view) : []),
    ];
    this.notifyWatchers("sources", this.sources);
    if (emitChange) {
      this.eventBus.emit("search.sources-changed", { sourceCount: this.sources.length }, this);
    }
  }

  private refreshSourceSubscriptions(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }

    if (!this.includeDefaultSources || !this.autoRefreshSources) {
      return;
    }

    const refreshEvents = [
      "map.layer-added",
      "map.layer-removed",
      "map.layers-added",
      "map.layers-cleared",
      "map.layer-reordered",
      "group-layer.layer-added",
      "group-layer.layer-removed",
      "group-layer.layers-added",
      "group-layer.layers-cleared",
    ] as const;
    for (const eventType of refreshEvents) {
      this.subscriptions.push(
        this.eventBus.on(eventType, () => {
          this.rebuildSources(true);
        }),
      );
    }
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(value);
    }
  }
}

interface GoToProvider {
  goTo(target: unknown): Promise<unknown> | unknown;
}

interface QueryFeaturesProvider {
  id?: unknown;
  title?: unknown;
  queryFeatures(options?: unknown): Promise<unknown> | unknown;
}

function isGoToProvider(value: unknown): value is GoToProvider {
  return isRecord(value) && typeof value.goTo === "function";
}

function resolveViewSearchSources(view: unknown): SearchSourceCompat[] {
  const map = extractMapFromView(view);
  const layers = extractLayersFromMap(map);
  const sources: SearchSourceCompat[] = [];
  for (const layer of layers) {
    if (!isQueryFeaturesProvider(layer)) {
      continue;
    }

    const source: SearchSourceCompat = {
      search: async ({ searchTerm }) => {
        const normalizedTerm = searchTerm.trim().toLowerCase();
        if (!normalizedTerm) {
          return [];
        }

        const response = await layer.queryFeatures({
          where: "1=1",
          outFields: ["*"],
          returnGeometry: true,
        });
        const features = extractFeatures(response);
        const matches: SearchResultCompat[] = [];
        for (const feature of features) {
          if (!featureMatchesTerm(feature, normalizedTerm)) {
            continue;
          }
          const name = describeFeature(feature, layer, matches.length);
          matches.push({
            name,
            feature,
            location: extractFeatureLocation(feature),
            source: layer,
          });
          if (matches.length >= 10) {
            break;
          }
        }
        return matches;
      },
      suggest: async ({ searchTerm }) => {
        const response = await source.search({ searchTerm });
        return response.slice(0, 5).map((result) => ({
          text: result.name,
          source: layer,
        }));
      },
    };
    sources.push(source);
  }
  return sources;
}

function extractMapFromView(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.map;
}

function extractLayersFromMap(map: unknown): unknown[] {
  if (!isRecord(map)) {
    return [];
  }
  if (Array.isArray(map.allLayers)) {
    return [...map.allLayers];
  }
  if (Array.isArray(map.layers)) {
    return [...map.layers];
  }
  return [];
}

function normalizeResultIndex(index: number, length: number): number {
  if (!Number.isFinite(index)) {
    return -1;
  }
  const normalized = Math.trunc(index);
  if (normalized < 0 || normalized >= length) {
    return -1;
  }
  return normalized;
}

function isQueryFeaturesProvider(value: unknown): value is QueryFeaturesProvider {
  return isRecord(value) && typeof value.queryFeatures === "function";
}

function extractFeatures(response: unknown): unknown[] {
  if (!isRecord(response) || !Array.isArray(response.features)) {
    return [];
  }
  return response.features;
}

function featureMatchesTerm(feature: unknown, normalizedTerm: string): boolean {
  if (!isRecord(feature) || !isRecord(feature.attributes)) {
    return false;
  }

  for (const value of Object.values(feature.attributes)) {
    if (value === null || value === undefined) {
      continue;
    }
    const text = String(value).toLowerCase();
    if (text.includes(normalizedTerm)) {
      return true;
    }
  }
  return false;
}

function describeFeature(feature: unknown, layer: QueryFeaturesProvider, index: number): string {
  if (isRecord(feature) && isRecord(feature.attributes)) {
    for (const key of ["name", "Name", "NAME", "title", "Title", "TITLE"]) {
      const value = feature.attributes[key];
      if (typeof value === "string" && value.trim().length > 0) {
        return value;
      }
    }
  }

  const layerTitle =
    typeof layer.title === "string"
      ? layer.title
      : typeof layer.id === "string"
        ? layer.id
        : "Result";
  return `${layerTitle} ${index + 1}`;
}

function extractFeatureLocation(feature: unknown): unknown {
  if (!isRecord(feature) || !isRecord(feature.geometry)) {
    return undefined;
  }

  const x = feature.geometry.x;
  const y = feature.geometry.y;
  if (typeof x === "number" && Number.isFinite(x) && typeof y === "number" && Number.isFinite(y)) {
    return { x, y };
  }
  return feature.geometry;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
