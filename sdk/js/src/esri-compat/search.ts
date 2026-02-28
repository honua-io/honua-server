import {
  CompatEventBus,
  type CompatEventSubscription,
  resolveCompatEventBus,
  safeInvokeCompatListener,
} from "./event-bus.js";

export interface SearchCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  sources?: readonly SearchSourceCompat[];
  includeDefaultSources?: boolean;
  autoNavigate?: boolean;
  autoRefreshSources?: boolean;
  defaultSourceMaxFeatureCandidates?: number;
  defaultSourceMaxResults?: number;
  defaultSourceMaxSuggestions?: number;
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

const DEFAULT_SEARCH_SOURCE_MAX_FEATURE_CANDIDATES = 200;
const DEFAULT_SEARCH_SOURCE_MAX_RESULTS = 10;
const DEFAULT_SEARCH_SOURCE_MAX_SUGGESTIONS = 5;

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
  public readonly defaultSourceMaxFeatureCandidates: number;
  public readonly defaultSourceMaxResults: number;
  public readonly defaultSourceMaxSuggestions: number;

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
    this.defaultSourceMaxFeatureCandidates = normalizeSearchLimit(
      options.defaultSourceMaxFeatureCandidates,
      DEFAULT_SEARCH_SOURCE_MAX_FEATURE_CANDIDATES,
    );
    this.defaultSourceMaxResults = normalizeSearchLimit(
      options.defaultSourceMaxResults,
      DEFAULT_SEARCH_SOURCE_MAX_RESULTS,
    );
    this.defaultSourceMaxSuggestions = normalizeSearchLimit(
      options.defaultSourceMaxSuggestions,
      DEFAULT_SEARCH_SOURCE_MAX_SUGGESTIONS,
    );
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

    const sourceResults = await Promise.all(
      this.sources.map(async (source) => {
        try {
          const results = await source.search({ searchTerm });
          return { source, results };
        } catch (error) {
          this.eventBus.emit("search.source-error", { searchTerm, error }, this);
          return { source, results: [] as readonly SearchResultCompat[] };
        }
      }),
    );

    const allResults: SearchResultCompat[] = [];
    for (const sourceResult of sourceResults) {
      for (const result of sourceResult.results) {
        allResults.push({
          ...result,
          source: result.source ?? sourceResult.source,
        });
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

    const suggestionSources = this.sources.filter((source) => typeof source.suggest === "function");
    const sourceSuggestions = await Promise.all(
      suggestionSources.map(async (source) => {
        if (!source.suggest) {
          return { source, suggestions: [] as readonly SearchSuggestionCompat[] };
        }
        try {
          const suggestions = await source.suggest({ searchTerm });
          return { source, suggestions };
        } catch (error) {
          this.eventBus.emit("search.suggest-error", { searchTerm, error }, this);
          return { source, suggestions: [] as readonly SearchSuggestionCompat[] };
        }
      }),
    );

    const suggestions: SearchSuggestionCompat[] = [];
    for (const sourceSuggestion of sourceSuggestions) {
      for (const suggestion of sourceSuggestion.suggestions) {
        suggestions.push({
          ...suggestion,
          source: suggestion.source ?? sourceSuggestion.source,
        });
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
      ...(this.includeDefaultSources
        ? resolveViewSearchSources(this.view, {
            maxFeatureCandidates: this.defaultSourceMaxFeatureCandidates,
            maxResults: this.defaultSourceMaxResults,
            maxSuggestions: this.defaultSourceMaxSuggestions,
          })
        : []),
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
      safeInvokeCompatListener(listener, value);
    }
  }
}

interface GoToProvider {
  goTo(target: unknown): Promise<unknown> | unknown;
}

interface QueryFeaturesProvider {
  id?: unknown;
  title?: unknown;
  outFields?: unknown;
  objectIdField?: unknown;
  displayField?: unknown;
  metadata?: unknown;
  fields?: unknown;
  listFields?(): readonly Record<string, unknown>[];
  queryFeatures(options?: unknown): Promise<unknown> | unknown;
}

interface OgcItemsProvider {
  id?: unknown;
  title?: unknown;
  collectionId?: unknown;
  items(options?: unknown): Promise<unknown> | unknown;
}

interface SearchSourceLimits {
  maxFeatureCandidates: number;
  maxResults: number;
  maxSuggestions: number;
}

type SearchLayerProvider = QueryFeaturesProvider | OgcItemsProvider;

interface LayerSearchFieldConfig {
  searchableFields: string[];
  outFields: string[];
}

function isGoToProvider(value: unknown): value is GoToProvider {
  return isRecord(value) && typeof value.goTo === "function";
}

function resolveViewSearchSources(view: unknown, limits: SearchSourceLimits): SearchSourceCompat[] {
  const map = extractMapFromView(view);
  const layers = extractLayersFromMap(map);
  const sources: SearchSourceCompat[] = [];
  for (const layer of layers) {
    if (isQueryFeaturesProvider(layer)) {
      let fieldSearchConfig = resolveLayerSearchFieldConfig(layer);
      let fieldSearchUnsupported = false;
      const source = buildLayerSearchSource(layer, limits, async (normalizedTerm) => {
        if (!fieldSearchUnsupported && fieldSearchConfig.searchableFields.length === 0) {
          fieldSearchConfig = resolveLayerSearchFieldConfig(layer);
        }

        const fallbackQueryOptions = createFallbackLayerSearchQueryOptions(
          limits.maxFeatureCandidates,
          normalizedTerm,
          fieldSearchConfig.outFields,
        );
        const optimizedQueryOptions =
          !fieldSearchUnsupported && fieldSearchConfig.searchableFields.length > 0
            ? createOptimizedLayerSearchQueryOptions(
                fieldSearchConfig,
                normalizedTerm,
                limits.maxFeatureCandidates,
              )
            : undefined;

        let response: unknown;
        if (optimizedQueryOptions) {
          try {
            response = await layer.queryFeatures(optimizedQueryOptions);
          } catch {
            fieldSearchUnsupported = true;
            response = await layer.queryFeatures(fallbackQueryOptions);
          }
        } else {
          response = await layer.queryFeatures(fallbackQueryOptions);
        }

        return extractFeatures(response);
      });
      sources.push(source);
      continue;
    }

    if (!isOgcItemsProvider(layer)) {
      continue;
    }

    const source = buildLayerSearchSource(layer, limits, async () => {
      const response = await layer.items({
        limit: limits.maxFeatureCandidates,
      });
      return extractFeatures(response);
    });
    sources.push(source);
  }
  return sources;
}

function buildLayerSearchSource(
  layer: SearchLayerProvider,
  limits: SearchSourceLimits,
  resolveFeatures: (normalizedTerm: string) => Promise<unknown[]>,
): SearchSourceCompat {
  let lastSearchTerm: string | undefined;
  let lastSearchResults: SearchResultCompat[] = [];
  let lastSearchPromise: Promise<SearchResultCompat[]> | undefined;

  const executeSearch = async (searchTerm: string): Promise<SearchResultCompat[]> => {
    const normalizedTerm = searchTerm.trim().toLowerCase();
    if (!normalizedTerm) {
      lastSearchTerm = undefined;
      lastSearchResults = [];
      lastSearchPromise = undefined;
      return [];
    }

    if (lastSearchTerm === normalizedTerm && lastSearchPromise) {
      return lastSearchPromise;
    }
    if (lastSearchTerm === normalizedTerm && lastSearchResults.length > 0) {
      return [...lastSearchResults];
    }

    const inFlight = (async (): Promise<SearchResultCompat[]> => {
      const features = await resolveFeatures(normalizedTerm);
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
        if (matches.length >= limits.maxResults) {
          break;
        }
      }
      lastSearchTerm = normalizedTerm;
      lastSearchResults = matches;
      return [...matches];
    })();

    lastSearchTerm = normalizedTerm;
    lastSearchPromise = inFlight;
    try {
      return await inFlight;
    } finally {
      if (lastSearchPromise === inFlight) {
        lastSearchPromise = undefined;
      }
    }
  };

  return {
    search: async ({ searchTerm }) => executeSearch(searchTerm),
    suggest: async ({ searchTerm }) => {
      const response = await executeSearch(searchTerm);
      return response.slice(0, limits.maxSuggestions).map((result) => ({
        text: result.name,
        source: layer,
      }));
    },
  };
}

const COMMON_SEARCH_FIELD_NAMES = [
  "name",
  "title",
  "label",
  "description",
  "city",
  "state",
  "county",
  "address",
] as const;
const MAX_SERVER_SEARCH_FIELDS = 6;
const MAX_SERVER_OUT_FIELDS = 16;

function createFallbackLayerSearchQueryOptions(
  maxFeatureCandidates: number,
  normalizedTerm: string,
  outFields: readonly string[],
): Record<string, unknown> {
  return {
    where: "1=1",
    outFields: normalizeFallbackOutFields(outFields),
    returnGeometry: true,
    extraParams: {
      text: normalizedTerm,
      resultOffset: 0,
      resultRecordCount: maxFeatureCandidates,
    },
  };
}

function createOptimizedLayerSearchQueryOptions(
  config: LayerSearchFieldConfig,
  normalizedTerm: string,
  maxFeatureCandidates: number,
): Record<string, unknown> {
  return {
    where: createSearchWhereClause(config.searchableFields, normalizedTerm),
    outFields: config.outFields,
    returnGeometry: true,
    extraParams: {
      resultOffset: 0,
      resultRecordCount: maxFeatureCandidates,
    },
  };
}

function createSearchWhereClause(searchableFields: readonly string[], normalizedTerm: string): string {
  const escapedTerm = escapeSqlLiteral(normalizedTerm.toUpperCase());
  return searchableFields
    .map((field) => `UPPER(${field}) LIKE '%${escapedTerm}%' ESCAPE '\\\\'`)
    .join(" OR ");
}

function resolveLayerSearchFieldConfig(layer: QueryFeaturesProvider): LayerSearchFieldConfig {
  const fieldDefinitions = resolveLayerFieldDefinitions(layer);
  const searchableFields = resolveSearchableFieldNames(layer, fieldDefinitions);
  const outFields = resolveSearchOutFields(layer, searchableFields, fieldDefinitions);
  return {
    searchableFields,
    outFields,
  };
}

interface LayerFieldDefinition {
  name: string;
  type: string | undefined;
}

function resolveLayerFieldDefinitions(layer: QueryFeaturesProvider): LayerFieldDefinition[] {
  const fromListFields = resolveLayerFieldsFromListFields(layer);
  if (fromListFields.length > 0) {
    return fromListFields;
  }
  return resolveLayerFieldsFromProperty(layer);
}

function resolveLayerFieldsFromListFields(layer: QueryFeaturesProvider): LayerFieldDefinition[] {
  if (typeof layer.listFields !== "function") {
    return [];
  }

  let fields: readonly Record<string, unknown>[];
  try {
    fields = layer.listFields();
  } catch {
    return [];
  }

  return normalizeLayerFieldDefinitions(fields);
}

function resolveLayerFieldsFromProperty(layer: QueryFeaturesProvider): LayerFieldDefinition[] {
  if (!Array.isArray(layer.fields)) {
    return [];
  }
  return normalizeLayerFieldDefinitions(layer.fields);
}

function normalizeLayerFieldDefinitions(values: readonly unknown[]): LayerFieldDefinition[] {
  const definitions: LayerFieldDefinition[] = [];
  const seen = new Set<string>();
  for (const value of values) {
    if (!isRecord(value) || typeof value.name !== "string") {
      continue;
    }

    const normalizedName = normalizeFieldIdentifier(value.name);
    if (!normalizedName) {
      continue;
    }
    const normalizedKey = normalizedName.toLowerCase();
    if (seen.has(normalizedKey)) {
      continue;
    }

    seen.add(normalizedKey);
    definitions.push({
      name: normalizedName,
      type: typeof value.type === "string" ? value.type : undefined,
    });
  }
  return definitions;
}

function resolveSearchableFieldNames(
  layer: QueryFeaturesProvider,
  fieldDefinitions: readonly LayerFieldDefinition[],
): string[] {
  const stringFields = fieldDefinitions.filter((field) => isStringFieldType(field.type)).map((field) => field.name);
  const searchable = new Set<string>();
  const addIfAvailable = (fieldName: string | undefined): void => {
    if (!fieldName) {
      return;
    }
    const available = fieldDefinitions.find((definition) => definition.name.toLowerCase() === fieldName.toLowerCase());
    if (!available) {
      return;
    }
    searchable.add(available.name);
  };

  addIfAvailable(resolveLayerDisplayField(layer));
  for (const commonName of COMMON_SEARCH_FIELD_NAMES) {
    addIfAvailable(commonName);
  }

  for (const fieldName of stringFields) {
    searchable.add(fieldName);
    if (searchable.size >= MAX_SERVER_SEARCH_FIELDS) {
      break;
    }
  }

  if (searchable.size > 0) {
    return Array.from(searchable).slice(0, MAX_SERVER_SEARCH_FIELDS);
  }

  for (const outField of resolveLayerOutFields(layer)) {
    const normalizedOutField = normalizeFieldIdentifier(outField);
    if (!normalizedOutField || normalizedOutField === "*") {
      continue;
    }
    searchable.add(normalizedOutField);
    if (searchable.size >= MAX_SERVER_SEARCH_FIELDS) {
      break;
    }
  }

  return Array.from(searchable);
}

function resolveSearchOutFields(
  layer: QueryFeaturesProvider,
  searchableFields: readonly string[],
  fieldDefinitions: readonly LayerFieldDefinition[],
): string[] {
  const outFields = new Set<string>();
  for (const field of searchableFields) {
    outFields.add(field);
  }

  const addIfAvailable = (fieldName: string | undefined): void => {
    if (!fieldName) {
      return;
    }
    const available = fieldDefinitions.find((definition) => definition.name.toLowerCase() === fieldName.toLowerCase());
    if (!available) {
      return;
    }
    outFields.add(available.name);
  };

  addIfAvailable(resolveLayerDisplayField(layer));
  addIfAvailable(resolveLayerObjectIdField(layer));
  for (const commonName of COMMON_SEARCH_FIELD_NAMES) {
    addIfAvailable(commonName);
  }

  const requestedOutFields = resolveLayerOutFields(layer);
  if (requestedOutFields.includes("*")) {
    return ["*"];
  }
  for (const field of requestedOutFields) {
    const normalizedField = normalizeFieldIdentifier(field);
    if (!normalizedField || normalizedField === "*") {
      continue;
    }
    outFields.add(normalizedField);
    if (outFields.size >= MAX_SERVER_OUT_FIELDS) {
      break;
    }
  }

  const resolved = Array.from(outFields).slice(0, MAX_SERVER_OUT_FIELDS);
  return resolved.length > 0 ? resolved : ["*"];
}

function resolveLayerOutFields(layer: QueryFeaturesProvider): string[] {
  if (typeof layer.outFields === "string") {
    return [layer.outFields];
  }
  if (Array.isArray(layer.outFields)) {
    return layer.outFields.filter((value): value is string => typeof value === "string");
  }
  return [];
}

function normalizeFallbackOutFields(outFields: readonly string[]): string[] {
  if (outFields.includes("*")) {
    return ["*"];
  }

  const normalized = new Set<string>();
  for (const outField of outFields) {
    const fieldName = normalizeFieldIdentifier(outField);
    if (!fieldName || fieldName === "*") {
      continue;
    }
    normalized.add(fieldName);
    if (normalized.size >= MAX_SERVER_OUT_FIELDS) {
      break;
    }
  }

  if (normalized.size > 0) {
    return Array.from(normalized);
  }
  return ["*"];
}

function resolveLayerDisplayField(layer: QueryFeaturesProvider): string | undefined {
  const fromLayer = normalizeFieldIdentifier(typeof layer.displayField === "string" ? layer.displayField : undefined);
  if (fromLayer) {
    return fromLayer;
  }
  if (!isRecord(layer.metadata)) {
    return undefined;
  }
  return normalizeFieldIdentifier(
    typeof layer.metadata.displayField === "string" ? layer.metadata.displayField : undefined,
  );
}

function resolveLayerObjectIdField(layer: QueryFeaturesProvider): string | undefined {
  const fromLayer = normalizeFieldIdentifier(typeof layer.objectIdField === "string" ? layer.objectIdField : undefined);
  if (fromLayer) {
    return fromLayer;
  }
  if (!isRecord(layer.metadata)) {
    return undefined;
  }
  if (typeof layer.metadata.objectIdField === "string") {
    return normalizeFieldIdentifier(layer.metadata.objectIdField);
  }
  return undefined;
}

function normalizeFieldIdentifier(fieldName: string | undefined): string | undefined {
  if (typeof fieldName !== "string") {
    return undefined;
  }
  const trimmed = fieldName.trim();
  if (trimmed.length === 0) {
    return undefined;
  }
  if (trimmed === "*") {
    return "*";
  }
  if (!/^[A-Za-z_][A-Za-z0-9_$.]*$/.test(trimmed)) {
    return undefined;
  }
  return trimmed;
}

function isStringFieldType(type: string | undefined): boolean {
  if (!type) {
    return true;
  }
  const normalized = type.trim().toLowerCase();
  if (normalized.length === 0) {
    return true;
  }
  return normalized.includes("string") || normalized.includes("text");
}

function escapeSqlLiteral(value: string): string {
  return value.replace(/\\/g, "\\\\").replace(/'/g, "''").replace(/%/g, "\\%").replace(/_/g, "\\_");
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

function isOgcItemsProvider(value: unknown): value is OgcItemsProvider {
  return isRecord(value) && typeof value.items === "function";
}

function normalizeSearchLimit(value: unknown, fallback: number): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return fallback;
  }
  return Math.max(1, Math.trunc(value));
}

function extractFeatures(response: unknown): unknown[] {
  if (!isRecord(response) || !Array.isArray(response.features)) {
    return [];
  }
  return response.features;
}

function featureMatchesTerm(feature: unknown, normalizedTerm: string): boolean {
  const properties = resolveFeatureProperties(feature);
  if (!properties) {
    return false;
  }

  for (const value of Object.values(properties)) {
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

function describeFeature(feature: unknown, layer: SearchLayerProvider, index: number): string {
  const properties = resolveFeatureProperties(feature);
  if (properties) {
    for (const key of ["name", "Name", "NAME", "title", "Title", "TITLE"]) {
      const value = properties[key];
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

  const point = extractPointFromGeoJsonGeometry(feature.geometry);
  if (point) {
    return point;
  }
  return feature.geometry;
}

function resolveFeatureProperties(feature: unknown): Record<string, unknown> | undefined {
  if (!isRecord(feature)) {
    return undefined;
  }
  if (isRecord(feature.attributes)) {
    return feature.attributes;
  }
  if (isRecord(feature.properties)) {
    return feature.properties;
  }
  return undefined;
}

function extractPointFromGeoJsonGeometry(geometry: Record<string, unknown>): { x: number; y: number } | undefined {
  const coordinates = extractPointCoordinates(geometry.coordinates);
  if (!coordinates) {
    return undefined;
  }
  return {
    x: coordinates[0],
    y: coordinates[1],
  };
}

function extractPointCoordinates(value: unknown): [number, number] | undefined {
  if (!Array.isArray(value) || value.length === 0) {
    return undefined;
  }
  const first = value[0];
  const second = value[1];
  if (typeof first === "number" && Number.isFinite(first) && typeof second === "number" && Number.isFinite(second)) {
    return [first, second];
  }

  if (Array.isArray(first)) {
    return extractPointCoordinates(first);
  }
  return undefined;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
