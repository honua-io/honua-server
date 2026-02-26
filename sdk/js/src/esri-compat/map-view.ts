import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface MapViewCompatOptions {
  map?: unknown;
  container?: unknown;
  center?: unknown;
  zoom?: number;
  popup?: unknown;
  eventBus?: CompatEventBus;
}

export interface MapViewGoToTarget {
  center?: unknown;
  zoom?: number;
}

export interface MapViewHandle {
  remove(): void;
}

export interface MapViewPopupOpenOptions {
  location?: unknown;
  features?: readonly unknown[];
  title?: string;
  content?: unknown;
}

export interface MapViewScreenPoint {
  x: number;
  y: number;
}

export interface MapViewMapPoint extends MapViewScreenPoint {
  spatialReference?: unknown;
}

export interface MapViewHitTestEvent {
  x?: number;
  y?: number;
  mapPoint?: MapViewMapPoint;
}

export interface MapViewHitTestResultItem {
  type: "graphic";
  graphic: unknown;
  layer?: unknown;
  mapPoint?: MapViewMapPoint;
}

export interface MapViewHitTestResult {
  results: MapViewHitTestResultItem[];
}

export type MapViewUiPosition = "manual" | "top-left" | "top-right" | "bottom-left" | "bottom-right" | string;

export interface MapViewUiAddOptions {
  position?: MapViewUiPosition;
  index?: number;
}

export interface MapViewUiComponentRecord {
  component: unknown;
  position: MapViewUiPosition;
  index: number;
}

type PopupChangeType = "open" | "close";

export interface MapViewPopupViewModelCompat {
  active: boolean;
}

export interface MapViewPopupCompatOptions {
  autoOpenEnabled?: boolean;
  dockEnabled?: boolean;
  dockOptions?: unknown;
}

export class MapViewPopupCompat {
  public visible: boolean;
  public location: unknown;
  public features: unknown[];
  public selectedFeature: unknown;
  public title: string | undefined;
  public content: unknown;
  public autoOpenEnabled: boolean;
  public dockEnabled: boolean;
  public dockOptions: unknown;
  public readonly viewModel: MapViewPopupViewModelCompat;

  private readonly onChange: (type: PopupChangeType, options?: MapViewPopupOpenOptions) => void;

  public constructor(
    onChange: (type: PopupChangeType, options?: MapViewPopupOpenOptions) => void,
    options: MapViewPopupCompatOptions = {},
  ) {
    this.visible = false;
    this.location = undefined;
    this.features = [];
    this.selectedFeature = undefined;
    this.title = undefined;
    this.content = undefined;
    this.autoOpenEnabled = options.autoOpenEnabled ?? true;
    this.dockEnabled = options.dockEnabled ?? false;
    this.dockOptions = options.dockOptions;
    this.viewModel = {
      active: false,
    };
    this.onChange = onChange;
  }

  public open(options: MapViewPopupOpenOptions = {}): void {
    this.visible = true;
    this.viewModel.active = true;
    this.location = options.location;
    this.features = options.features ? [...options.features] : [];
    this.selectedFeature = this.features[0];
    this.title = options.title;
    this.content = options.content;
    this.onChange("open", options);
  }

  public close(): void {
    if (!this.visible) {
      return;
    }

    this.visible = false;
    this.viewModel.active = false;
    this.location = undefined;
    this.features = [];
    this.selectedFeature = undefined;
    this.title = undefined;
    this.content = undefined;
    this.onChange("close");
  }
}

export class MapViewLayerViewCompat {
  public readonly layer: unknown;
  public updating: boolean;
  public suspended: boolean;
  public hasAllFeatures: boolean;
  public hasAllFeaturesInView: boolean;

  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private readonly eventBus: CompatEventBus | undefined;

  public constructor(layer: unknown, eventBus?: CompatEventBus) {
    this.layer = layer;
    this.updating = false;
    this.suspended = false;
    this.hasAllFeatures = true;
    this.hasAllFeaturesInView = true;
    this.watchListeners = new Map();
    this.eventBus = eventBus;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): MapViewHandle {
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

  public setUpdating(value: boolean): void {
    this.updating = value;
    this.notifyWatchers("updating", value);
    this.eventBus?.emit("view.layer-view-updating-changed", { layer: this.layer, updating: value }, this);
  }

  public setSuspended(value: boolean): void {
    this.suspended = value;
    this.notifyWatchers("suspended", value);
    this.eventBus?.emit("view.layer-view-suspended-changed", { layer: this.layer, suspended: value }, this);
  }

  public setHasAllFeatures(value: boolean): void {
    this.hasAllFeatures = value;
    this.notifyWatchers("hasAllFeatures", value);
    this.eventBus?.emit("view.layer-view-has-all-features-changed", { layer: this.layer, hasAllFeatures: value }, this);
  }

  public setHasAllFeaturesInView(value: boolean): void {
    this.hasAllFeaturesInView = value;
    this.notifyWatchers("hasAllFeaturesInView", value);
    this.eventBus?.emit(
      "view.layer-view-has-all-features-in-view-changed",
      { layer: this.layer, hasAllFeaturesInView: value },
      this,
    );
  }

  public async queryFeatures(options: unknown = {}): Promise<unknown> {
    if (isQueryFeaturesProvider(this.layer)) {
      return this.layer.queryFeatures(options);
    }

    return { features: [] };
  }

  public async queryFeatureCount(options: unknown = {}): Promise<number> {
    if (isQueryFeatureCountProvider(this.layer)) {
      const count = await this.layer.queryFeatureCount(options);
      return normalizeCount(count);
    }

    const result = await this.queryFeatures(options);
    if (isFeatureCollection(result)) {
      return result.features.length;
    }

    return 0;
  }

  public async queryObjectIds(options: unknown = {}): Promise<number[]> {
    if (isQueryObjectIdsProvider(this.layer)) {
      const ids = await this.layer.queryObjectIds(options);
      return Array.isArray(ids)
        ? ids.filter((value): value is number => typeof value === "number" && Number.isFinite(value))
        : [];
    }

    const result = await this.queryFeatures(options);
    if (!isFeatureCollection(result)) {
      return [];
    }

    const ids: number[] = [];
    for (const feature of result.features) {
      const objectId = extractObjectId(feature);
      if (typeof objectId === "number" && Number.isFinite(objectId)) {
        ids.push(objectId);
      }
    }
    return ids;
  }

  public destroy(): void {
    this.watchListeners.clear();
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

interface QueryFeaturesProvider {
  queryFeatures(options?: unknown): Promise<unknown> | unknown;
}

interface QueryFeatureCountProvider {
  queryFeatureCount(options?: unknown): Promise<number> | number;
}

interface QueryObjectIdsProvider {
  queryObjectIds(options?: unknown): Promise<number[]> | number[];
}

export class MapViewUiCompat {
  private readonly eventBus: CompatEventBus;
  private readonly onChanged:
    | ((components: readonly MapViewUiComponentRecord[]) => void)
    | undefined;
  private readonly componentsInternal: MapViewUiComponentRecord[];

  public constructor(
    eventBus: CompatEventBus,
    onChanged?: (components: readonly MapViewUiComponentRecord[]) => void,
  ) {
    this.eventBus = eventBus;
    this.onChanged = onChanged;
    this.componentsInternal = [];
  }

  public get components(): readonly MapViewUiComponentRecord[] {
    return this.componentsInternal.map((record) => ({ ...record }));
  }

  public add(
    componentOrComponents: unknown | readonly unknown[],
    positionOrOptions: MapViewUiPosition | MapViewUiAddOptions = "manual",
  ): void {
    if (Array.isArray(componentOrComponents)) {
      for (const component of componentOrComponents) {
        this.add(component, positionOrOptions);
      }
      return;
    }

    const component = componentOrComponents;
    const options = normalizeUiOptions(positionOrOptions);
    const existingIndex = this.findComponentIndex(component);
    if (existingIndex >= 0) {
      this.componentsInternal.splice(existingIndex, 1);
    }

    const insertIndex = normalizeUiIndex(options.index, this.componentsInternal.length);
    const record: MapViewUiComponentRecord = {
      component,
      position: options.position,
      index: insertIndex,
    };
    this.componentsInternal.splice(insertIndex, 0, record);
    this.reindexComponents();
    this.eventBus.emit(
      "view.ui.component-added",
      {
        component,
        position: record.position,
        index: record.index,
      },
      this,
    );
    this.notifyChanged();
  }

  public remove(componentOrId: unknown): boolean {
    const index = this.findComponentIndex(componentOrId);
    if (index < 0) {
      return false;
    }

    const [removed] = this.componentsInternal.splice(index, 1);
    this.reindexComponents();
    this.eventBus.emit(
      "view.ui.component-removed",
      {
        component: removed?.component,
        position: removed?.position,
      },
      this,
    );
    this.notifyChanged();
    return true;
  }

  public removeAll(): void {
    if (this.componentsInternal.length === 0) {
      return;
    }

    const removedComponents = this.componentsInternal.map((record) => record.component);
    this.componentsInternal.length = 0;
    this.eventBus.emit(
      "view.ui.components-cleared",
      {
        count: removedComponents.length,
      },
      this,
    );
    this.notifyChanged();
  }

  public empty(position: MapViewUiPosition): void {
    const previous = this.componentsInternal.length;
    const remaining = this.componentsInternal.filter((record) => record.position !== position);
    if (remaining.length === previous) {
      return;
    }

    this.componentsInternal.length = 0;
    this.componentsInternal.push(...remaining);
    this.reindexComponents();
    this.eventBus.emit(
      "view.ui.position-cleared",
      {
        position,
        remainingCount: this.componentsInternal.length,
      },
      this,
    );
    this.notifyChanged();
  }

  public move(
    componentOrId: unknown,
    positionOrOptions: MapViewUiPosition | MapViewUiAddOptions,
  ): boolean {
    const index = this.findComponentIndex(componentOrId);
    if (index < 0) {
      return false;
    }

    const [record] = this.componentsInternal.splice(index, 1);
    const options = normalizeUiOptions(positionOrOptions);
    const insertIndex = normalizeUiIndex(options.index, this.componentsInternal.length);
    const movedRecord: MapViewUiComponentRecord = {
      component: record!.component,
      position: options.position,
      index: insertIndex,
    };
    this.componentsInternal.splice(insertIndex, 0, movedRecord);
    this.reindexComponents();
    this.eventBus.emit(
      "view.ui.component-moved",
      {
        component: movedRecord.component,
        position: movedRecord.position,
        index: movedRecord.index,
      },
      this,
    );
    this.notifyChanged();
    return true;
  }

  public find(componentOrId: unknown): unknown {
    const index = this.findComponentIndex(componentOrId);
    return index < 0 ? undefined : this.componentsInternal[index]?.component;
  }

  public getComponents(position?: MapViewUiPosition): unknown[] {
    if (position === undefined) {
      return this.componentsInternal.map((record) => record.component);
    }
    return this.componentsInternal
      .filter((record) => record.position === position)
      .map((record) => record.component);
  }

  private findComponentIndex(componentOrId: unknown): number {
    if (typeof componentOrId === "string") {
      for (let i = 0; i < this.componentsInternal.length; i += 1) {
        const component = this.componentsInternal[i]?.component;
        if (isRecord(component) && component.id === componentOrId) {
          return i;
        }
      }
      return -1;
    }

    return this.componentsInternal.findIndex((record) => record.component === componentOrId);
  }

  private reindexComponents(): void {
    for (let i = 0; i < this.componentsInternal.length; i += 1) {
      const existing = this.componentsInternal[i];
      if (!existing) {
        continue;
      }
      existing.index = i;
    }
  }

  private notifyChanged(): void {
    this.onChanged?.(this.components);
  }
}

export class MapViewCompat {
  public map: unknown;
  public container: unknown;
  public center: unknown;
  public zoom: number | undefined;
  public readonly eventBus: CompatEventBus;
  public readonly popup: MapViewPopupCompat;
  public readonly ui: MapViewUiCompat;

  private readonly eventListeners: Map<string, Set<(event: unknown) => void>>;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private readonly layerViews: Map<unknown, MapViewLayerViewCompat>;
  private readonly readyPromise: Promise<MapViewCompat>;

  public constructor(options: MapViewCompatOptions = {}) {
    this.map = options.map;
    this.container = options.container;
    this.center = options.center;
    this.zoom = options.zoom;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.map, options.container) ?? new CompatEventBus();
    this.popup = new MapViewPopupCompat((type, popupOptions) => {
      this.notifyWatchers("popup.visible", this.popup.visible);
      this.notifyWatchers("popup.features", this.popup.features);
      this.notifyWatchers("popup.selectedFeature", this.popup.selectedFeature);
      this.notifyWatchers("popup.location", this.popup.location);
      this.notifyWatchers("popup.title", this.popup.title);
      this.notifyWatchers("popup.content", this.popup.content);
      this.notifyWatchers("popup.viewModel.active", this.popup.viewModel.active);
      this.eventBus.emit(type === "open" ? "popup.open" : "popup.close", popupOptions, this);
      this.emit(type === "open" ? "popup-open" : "popup-close", popupOptions);
    }, extractPopupOptions(options.popup));
    this.ui = new MapViewUiCompat(this.eventBus, (components) => {
      this.notifyWatchers("ui.components", components);
    });
    this.eventListeners = new Map();
    this.watchListeners = new Map();
    this.layerViews = new Map();
    this.readyPromise = Promise.resolve(this);
  }

  public async when(callback?: (view: MapViewCompat) => void): Promise<MapViewCompat> {
    const view = await this.readyPromise;
    if (callback) {
      callback(view);
    }

    return view;
  }

  public toMap(screenPoint: MapViewScreenPoint): MapViewMapPoint {
    return {
      x: screenPoint.x,
      y: screenPoint.y,
    };
  }

  public toScreen(mapPoint: MapViewMapPoint): MapViewScreenPoint {
    return {
      x: mapPoint.x,
      y: mapPoint.y,
    };
  }

  public async hitTest(event: MapViewHitTestEvent = {}): Promise<MapViewHitTestResult> {
    const mapPoint =
      event.mapPoint ??
      (typeof event.x === "number" && typeof event.y === "number"
        ? this.toMap({ x: event.x, y: event.y })
        : undefined);

    return {
      results: this.popup.features.map((feature) => ({
        type: "graphic",
        graphic: feature,
        layer: extractGraphicLayer(feature),
        mapPoint,
      })),
    };
  }

  public async goTo(target: MapViewGoToTarget): Promise<MapViewCompat> {
    if (target.center !== undefined) {
      this.center = target.center;
      this.notifyWatchers("center", this.center);
    }
    if (target.zoom !== undefined) {
      this.zoom = target.zoom;
      this.notifyWatchers("zoom", this.zoom);
    }
    this.eventBus.emit("view.go-to", target, this);
    this.emit("go-to", target);

    return this;
  }

  public openPopup(options: MapViewPopupOpenOptions = {}): void {
    this.popup.open(options);
  }

  public closePopup(): void {
    this.popup.close();
  }

  public async whenLayerView(layer: unknown): Promise<MapViewLayerViewCompat> {
    const existing = this.layerViews.get(layer);
    if (existing) {
      return existing;
    }

    const layerView = new MapViewLayerViewCompat(layer, this.eventBus);
    this.layerViews.set(layer, layerView);
    this.eventBus.emit("view.layer-view-created", { layer, layerView }, this);
    this.emit("layerview-create", { layer, layerView });
    return layerView;
  }

  public on(eventName: string, listener: (event: unknown) => void): MapViewHandle {
    let listeners = this.eventListeners.get(eventName);
    if (!listeners) {
      listeners = new Set();
      this.eventListeners.set(eventName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public watch(propertyName: string, listener: (value: unknown) => void): MapViewHandle {
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

  public destroy(): void {
    this.eventBus.emit("view.destroy", undefined, this);
    this.emit("destroy", undefined);
    this.ui.removeAll();
    for (const layerView of this.layerViews.values()) {
      layerView.destroy();
    }
    this.layerViews.clear();
    this.closePopup();
    this.map = undefined;
    this.notifyWatchers("map", this.map);
    this.container = undefined;
    this.notifyWatchers("container", this.container);
    this.center = undefined;
    this.notifyWatchers("center", this.center);
    this.zoom = undefined;
    this.notifyWatchers("zoom", this.zoom);
    this.eventListeners.clear();
    this.watchListeners.clear();
  }

  private emit(eventName: string, payload: unknown): void {
    const listeners = this.eventListeners.get(eventName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(payload);
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

function normalizeUiOptions(input: MapViewUiPosition | MapViewUiAddOptions): Required<MapViewUiAddOptions> {
  if (typeof input === "string") {
    return {
      position: input,
      index: Number.NaN,
    };
  }

  const position =
    typeof input.position === "string" && input.position.length > 0 ? input.position : "manual";
  const index =
    typeof input.index === "number" && Number.isFinite(input.index) ? Math.trunc(input.index) : Number.NaN;
  return {
    position,
    index,
  };
}

function normalizeUiIndex(index: number, length: number): number {
  if (!Number.isFinite(index)) {
    return length;
  }
  return Math.min(Math.max(index, 0), length);
}

function isQueryFeaturesProvider(value: unknown): value is QueryFeaturesProvider {
  return (
    typeof value === "object" &&
    value !== null &&
    "queryFeatures" in value &&
    typeof value.queryFeatures === "function"
  );
}

function isQueryFeatureCountProvider(value: unknown): value is QueryFeatureCountProvider {
  return (
    typeof value === "object" &&
    value !== null &&
    "queryFeatureCount" in value &&
    typeof value.queryFeatureCount === "function"
  );
}

function isQueryObjectIdsProvider(value: unknown): value is QueryObjectIdsProvider {
  return (
    typeof value === "object" &&
    value !== null &&
    "queryObjectIds" in value &&
    typeof value.queryObjectIds === "function"
  );
}

function isFeatureCollection(value: unknown): value is { features: unknown[] } {
  return typeof value === "object" && value !== null && "features" in value && Array.isArray(value.features);
}

function extractObjectId(feature: unknown): number | undefined {
  if (typeof feature !== "object" || feature === null) {
    return undefined;
  }

  if ("objectId" in feature && typeof feature.objectId === "number") {
    return feature.objectId;
  }

  if ("attributes" in feature && typeof feature.attributes === "object" && feature.attributes !== null) {
    if (
      "OBJECTID" in feature.attributes &&
      typeof feature.attributes.OBJECTID === "number" &&
      Number.isFinite(feature.attributes.OBJECTID)
    ) {
      return feature.attributes.OBJECTID;
    }
    if (
      "objectid" in feature.attributes &&
      typeof feature.attributes.objectid === "number" &&
      Number.isFinite(feature.attributes.objectid)
    ) {
      return feature.attributes.objectid;
    }
    if (
      "objectId" in feature.attributes &&
      typeof feature.attributes.objectId === "number" &&
      Number.isFinite(feature.attributes.objectId)
    ) {
      return feature.attributes.objectId;
    }
  }

  return undefined;
}

function normalizeCount(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function extractGraphicLayer(value: unknown): unknown {
  if (typeof value !== "object" || value === null) {
    return undefined;
  }
  if (!("layer" in value)) {
    return undefined;
  }
  return value.layer;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}

function extractPopupOptions(popup: unknown): MapViewPopupCompatOptions {
  if (!isRecord(popup)) {
    return {};
  }

  return {
    autoOpenEnabled:
      typeof popup.autoOpenEnabled === "boolean" ? popup.autoOpenEnabled : undefined,
    dockEnabled:
      typeof popup.dockEnabled === "boolean" ? popup.dockEnabled : undefined,
    dockOptions: popup.dockOptions,
  };
}
