import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

// ── Structural Type Aliases ───────────────────────────────────

/** A center value: either a point-like object with x/y or a coordinate pair. */
export type MapViewCenterLike =
  | { x: number; y: number; spatialReference?: MapViewSpatialReferenceLike }
  | [number, number];

/** An extent-like bounding box with optional spatial reference. */
export interface MapViewExtentLike {
  xmin: number;
  ymin: number;
  xmax: number;
  ymax: number;
  spatialReference?: MapViewSpatialReferenceLike;
}

/** Padding around the view in CSS pixels. */
export interface MapViewPaddingLike {
  left?: number;
  right?: number;
  top?: number;
  bottom?: number;
}

/** View constraints such as zoom and scale limits. */
export interface MapViewConstraintsLike {
  minZoom?: number;
  maxZoom?: number;
  minScale?: number;
  maxScale?: number;
  [key: string]: unknown;
}

/** Highlight options controlling color and opacity of highlighted features. */
export interface MapViewHighlightOptionsLike {
  color?: string | number[];
  haloColor?: string | number[];
  haloOpacity?: number;
  fillOpacity?: number;
  [key: string]: unknown;
}

/** A spatial reference identified by WKID or WKT. */
export interface MapViewSpatialReferenceLike {
  wkid?: number;
  latestWkid?: number;
  wkt?: string;
  [key: string]: unknown;
}

// ── Options & Input Interfaces ────────────────────────────────

export interface MapViewCompatOptions {
  map?: unknown;
  container?: HTMLElement | string | null;
  center?: MapViewCenterLike;
  zoom?: number;
  scale?: number;
  rotation?: number;
  extent?: MapViewExtentLike;
  constraints?: MapViewConstraintsLike;
  padding?: MapViewPaddingLike;
  highlightOptions?: MapViewHighlightOptionsLike;
  spatialReference?: MapViewSpatialReferenceLike;
  popup?: unknown;
  eventBus?: CompatEventBus;
}

export interface MapViewGoToTarget {
  target?: MapViewGoToInput;
  center?: MapViewCenterLike;
  zoom?: number;
  scale?: number;
  rotation?: number;
  extent?: MapViewExtentLike;
}

export interface MapViewGoToPointLike {
  x?: number;
  y?: number;
  longitude?: number;
  latitude?: number;
  spatialReference?: MapViewSpatialReferenceLike;
}

export interface MapViewGoToExtentLike {
  xmin: number;
  ymin: number;
  xmax: number;
  ymax: number;
  spatialReference?: MapViewSpatialReferenceLike;
}

export type MapViewGoToInput =
  | MapViewGoToTarget
  | MapViewGoToPointLike
  | MapViewGoToExtentLike
  | readonly [number, number]
  | readonly Record<string, unknown>[]
  | Record<string, unknown>;

export interface MapViewGoToOptions {
  animate?: boolean;
  duration?: number;
  speedFactor?: number;
  easing?: string;
}

export interface MapViewTakeScreenshotArea {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface MapViewTakeScreenshotOptions {
  width?: number;
  height?: number;
  format?: "png" | "jpg" | "jpeg";
  quality?: number;
  area?: MapViewTakeScreenshotArea;
  ignoreBackground?: boolean;
}

export interface MapViewTakeScreenshotResult {
  data: Uint8ClampedArray;
  dataUrl: string;
  width: number;
  height: number;
}

export interface MapViewHandle {
  remove(): void;
}

export type MapViewLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface MapViewPopupOpenOptions {
  location?: MapViewCenterLike | Record<string, unknown>;
  features?: readonly Record<string, unknown>[];
  title?: string;
  content?: string | HTMLElement | Record<string, unknown>;
}

export interface MapViewScreenPoint {
  x: number;
  y: number;
}

export interface MapViewMapPoint extends MapViewScreenPoint {
  spatialReference?: MapViewSpatialReferenceLike;
}

export interface MapViewHitTestEvent {
  x?: number;
  y?: number;
  mapPoint?: MapViewMapPoint;
}

export interface MapViewHitTestResultItem {
  type: "graphic";
  graphic: Record<string, unknown>;
  layer?: Record<string, unknown>;
  mapPoint?: MapViewMapPoint;
}

export interface MapViewHitTestResult {
  results: MapViewHitTestResultItem[];
}

export interface MapViewLayerViewHighlightOptions {
  name?: string;
}

export interface MapViewLayerViewHighlightRecord {
  targets: (Record<string, unknown> | number | string)[];
  options: MapViewLayerViewHighlightOptions;
}

export interface MapViewLayerViewHighlightHandle extends MapViewHandle {}

export type MapViewUiPosition = "manual" | "top-left" | "top-right" | "bottom-left" | "bottom-right" | string;

export interface MapViewUiAddOptions {
  position?: MapViewUiPosition;
  index?: number;
}

export interface MapViewUiComponentRecord {
  component: Record<string, unknown> | HTMLElement;
  position: MapViewUiPosition;
  index: number;
}

type PopupChangeType = "open" | "close" | "selection";

export interface MapViewPopupViewModelCompat {
  active: boolean;
}

export interface MapViewPopupCompatOptions {
  autoOpenEnabled?: boolean;
  dockEnabled?: boolean;
  dockOptions?: Record<string, unknown>;
}

export class MapViewPopupCompat {
  public visible: boolean;
  public location: MapViewCenterLike | Record<string, unknown> | undefined;
  public features: Record<string, unknown>[];
  public selectedFeature: Record<string, unknown> | undefined;
  public selectedFeatureIndex: number;
  public title: string | undefined;
  public content: string | HTMLElement | Record<string, unknown> | undefined;
  public autoOpenEnabled: boolean;
  public dockEnabled: boolean;
  public dockOptions: Record<string, unknown> | undefined;
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
    this.selectedFeatureIndex = -1;
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
    this.selectedFeatureIndex = this.features.length > 0 ? 0 : -1;
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
    this.selectedFeatureIndex = -1;
    this.title = undefined;
    this.content = undefined;
    this.onChange("close");
  }

  public selectFeature(featureOrIndex: Record<string, unknown> | number): Record<string, unknown> | undefined {
    if (this.features.length === 0) {
      return undefined;
    }

    const index =
      typeof featureOrIndex === "number"
        ? normalizeFeatureIndex(featureOrIndex, this.features.length)
        : this.features.findIndex((feature) => feature === featureOrIndex);
    if (index < 0) {
      return undefined;
    }

    this.selectedFeatureIndex = index;
    this.selectedFeature = this.features[index];
    this.onChange("selection");
    return this.selectedFeature;
  }

  public next(): Record<string, unknown> | undefined {
    if (this.features.length === 0) {
      return undefined;
    }
    const current = this.selectedFeatureIndex >= 0 ? this.selectedFeatureIndex : 0;
    const next = Math.min(current + 1, this.features.length - 1);
    return this.selectFeature(next);
  }

  public previous(): Record<string, unknown> | undefined {
    if (this.features.length === 0) {
      return undefined;
    }
    const current = this.selectedFeatureIndex >= 0 ? this.selectedFeatureIndex : 0;
    const previous = Math.max(current - 1, 0);
    return this.selectFeature(previous);
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
  private readonly highlightsInternal: Map<number, MapViewLayerViewHighlightRecord>;
  private nextHighlightId: number;

  public constructor(layer: unknown, eventBus?: CompatEventBus) {
    this.layer = layer;
    this.updating = false;
    this.suspended = false;
    this.hasAllFeatures = true;
    this.hasAllFeaturesInView = true;
    this.watchListeners = new Map();
    this.eventBus = eventBus;
    this.highlightsInternal = new Map();
    this.nextHighlightId = 1;
  }

  public get highlights(): readonly MapViewLayerViewHighlightRecord[] {
    return Array.from(this.highlightsInternal.values()).map((record) => ({
      targets: [...record.targets],
      options: { ...record.options },
    }));
  }

  public watch(propertyName: "updating", listener: (value: boolean) => void): MapViewHandle;
  public watch(propertyName: "suspended", listener: (value: boolean) => void): MapViewHandle;
  public watch(propertyName: "hasAllFeatures", listener: (value: boolean) => void): MapViewHandle;
  public watch(propertyName: "hasAllFeaturesInView", listener: (value: boolean) => void): MapViewHandle;
  public watch(
    propertyName: "highlights",
    listener: (value: readonly MapViewLayerViewHighlightRecord[]) => void,
  ): MapViewHandle;
  public watch(propertyName: string, listener: (value: unknown) => void): MapViewHandle;
  public watch(propertyName: string, listener: (value: any) => void): MapViewHandle {
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

  public async queryFeatures(options: Record<string, unknown> = {}): Promise<Record<string, unknown>> {
    if (isQueryFeaturesProvider(this.layer)) {
      return this.layer.queryFeatures(options);
    }

    return { features: [] };
  }

  public async queryFeatureCount(options: Record<string, unknown> = {}): Promise<number> {
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

  public async queryObjectIds(options: Record<string, unknown> = {}): Promise<number[]> {
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

  public highlight(
    target: Record<string, unknown> | number | string | readonly (Record<string, unknown> | number | string)[],
    options: MapViewLayerViewHighlightOptions = {},
  ): MapViewLayerViewHighlightHandle {
    const id = this.nextHighlightId;
    this.nextHighlightId += 1;
    const record: MapViewLayerViewHighlightRecord = {
      targets: normalizeHighlightTargets(target),
      options: { ...options },
    };
    this.highlightsInternal.set(id, record);
    this.notifyWatchers("highlights", this.highlights);
    this.eventBus?.emit(
      "view.layer-view-highlight-added",
      {
        layer: this.layer,
        targets: [...record.targets],
        options: { ...record.options },
        count: this.highlightsInternal.size,
      },
      this,
    );

    return {
      remove: () => {
        this.removeHighlight(id);
      },
    };
  }

  public destroy(): void {
    if (this.highlightsInternal.size > 0) {
      this.highlightsInternal.clear();
      this.notifyWatchers("highlights", this.highlights);
      this.eventBus?.emit("view.layer-view-highlights-cleared", { layer: this.layer, count: 0 }, this);
    }
    this.watchListeners.clear();
  }

  private removeHighlight(id: number): boolean {
    const record = this.highlightsInternal.get(id);
    if (!record) {
      return false;
    }

    this.highlightsInternal.delete(id);
    this.notifyWatchers("highlights", this.highlights);
    this.eventBus?.emit(
      "view.layer-view-highlight-removed",
      {
        layer: this.layer,
        targets: [...record.targets],
        options: { ...record.options },
        count: this.highlightsInternal.size,
      },
      this,
    );
    return true;
  }

  protected notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeListener(() => listener(value));
    }
  }
}

interface QueryFeaturesProvider {
  queryFeatures(options?: Record<string, unknown>): Promise<Record<string, unknown>> | Record<string, unknown>;
}

interface QueryFeatureCountProvider {
  queryFeatureCount(options?: unknown): Promise<number> | number;
}

interface QueryObjectIdsProvider {
  queryObjectIds(options?: unknown): Promise<number[]> | number[];
}

export class MapViewUiCompat {
  private readonly eventBus: CompatEventBus;
  private readonly onChanged: ((components: readonly MapViewUiComponentRecord[]) => void) | undefined;
  private readonly componentsInternal: MapViewUiComponentRecord[];

  public constructor(eventBus: CompatEventBus, onChanged?: (components: readonly MapViewUiComponentRecord[]) => void) {
    this.eventBus = eventBus;
    this.onChanged = onChanged;
    this.componentsInternal = [];
  }

  public get components(): readonly MapViewUiComponentRecord[] {
    return this.componentsInternal.map((record) => ({ ...record }));
  }

  public add(
    componentOrComponents: Record<string, unknown> | HTMLElement | readonly (Record<string, unknown> | HTMLElement)[],
    positionOrOptions: MapViewUiPosition | MapViewUiAddOptions = "manual",
  ): void {
    if (Array.isArray(componentOrComponents)) {
      for (const component of componentOrComponents as (Record<string, unknown> | HTMLElement)[]) {
        this.add(component, positionOrOptions);
      }
      return;
    }

    const component = componentOrComponents as Record<string, unknown> | HTMLElement;
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

  public remove(componentOrId: Record<string, unknown> | HTMLElement | string): boolean {
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
    componentOrId: Record<string, unknown> | HTMLElement | string,
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

  public find(
    componentOrId: Record<string, unknown> | HTMLElement | string,
  ): Record<string, unknown> | HTMLElement | undefined {
    const index = this.findComponentIndex(componentOrId);
    return index < 0 ? undefined : this.componentsInternal[index]?.component;
  }

  public getComponents(position?: MapViewUiPosition): (Record<string, unknown> | HTMLElement)[] {
    if (position === undefined) {
      return this.componentsInternal.map((record) => record.component);
    }
    return this.componentsInternal.filter((record) => record.position === position).map((record) => record.component);
  }

  private findComponentIndex(componentOrId: Record<string, unknown> | HTMLElement | string): number {
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
  public container: HTMLElement | string | null | undefined;
  public loaded: boolean;
  public loadStatus: MapViewLoadStatusCompat;
  public center: MapViewCenterLike | undefined;
  public zoom: number | undefined;
  public scale: number | undefined;
  public rotation: number | undefined;
  public extent: MapViewExtentLike | undefined;
  public constraints: MapViewConstraintsLike | undefined;
  public padding: MapViewPaddingLike | undefined;
  public highlightOptions: MapViewHighlightOptionsLike | undefined;
  public spatialReference: MapViewSpatialReferenceLike | undefined;
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
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.center = options.center;
    this.zoom = options.zoom;
    this.scale = options.scale;
    this.rotation = options.rotation;
    this.extent = options.extent;
    this.constraints = options.constraints;
    this.padding = options.padding;
    this.highlightOptions = options.highlightOptions;
    this.spatialReference = options.spatialReference;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.map, options.container) ?? new CompatEventBus();
    this.popup = new MapViewPopupCompat((type, popupOptions) => {
      this.notifyWatchers("popup.visible", this.popup.visible);
      this.notifyWatchers("popup.features", this.popup.features);
      this.notifyWatchers("popup.selectedFeature", this.popup.selectedFeature);
      this.notifyWatchers("popup.selectedFeatureIndex", this.popup.selectedFeatureIndex);
      this.notifyWatchers("popup.location", this.popup.location);
      this.notifyWatchers("popup.title", this.popup.title);
      this.notifyWatchers("popup.content", this.popup.content);
      this.notifyWatchers("popup.viewModel.active", this.popup.viewModel.active);
      if (type === "open") {
        this.eventBus.emit("popup.open", popupOptions, this);
        this.emit("popup-open", popupOptions);
      } else if (type === "close") {
        this.eventBus.emit("popup.close", popupOptions, this);
        this.emit("popup-close", popupOptions);
      } else {
        const selection = {
          selectedFeature: this.popup.selectedFeature,
          selectedFeatureIndex: this.popup.selectedFeatureIndex,
        };
        this.eventBus.emit("popup.selected-feature-changed", selection, this);
        this.emit("popup-selection-change", selection);
      }
    }, extractPopupOptions(options.popup));
    this.ui = new MapViewUiCompat(this.eventBus, (components) => {
      this.notifyWatchers("ui.components", components);
    });
    this.eventListeners = new Map();
    this.watchListeners = new Map();
    this.layerViews = new Map();
    this.readyPromise = Promise.resolve(this);
  }

  public async load(): Promise<MapViewCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("view.loading", undefined, this);
    await this.readyPromise;
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("view.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (view: MapViewCompat) => void): Promise<MapViewCompat> {
    const view = await this.load();
    if (callback) {
      callback(view);
    }

    return view;
  }

  public toMap(screenPoint: MapViewScreenPoint): MapViewMapPoint {
    const mapPoint: MapViewMapPoint = {
      x: screenPoint.x,
      y: screenPoint.y,
    };
    if (this.spatialReference !== undefined) {
      mapPoint.spatialReference = this.spatialReference;
    }
    return mapPoint;
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
      (typeof event.x === "number" && typeof event.y === "number" ? this.toMap({ x: event.x, y: event.y }) : undefined);

    return {
      results: this.popup.features.map((feature) => ({
        type: "graphic",
        graphic: feature,
        layer: extractGraphicLayer(feature),
        mapPoint,
      })),
    };
  }

  public async goTo(target: MapViewGoToInput, options: MapViewGoToOptions = {}): Promise<MapViewCompat> {
    const normalizedTarget = normalizeGoToTarget(target);
    if (normalizedTarget.center !== undefined) {
      this.setCenter(normalizedTarget.center);
    }
    if (normalizedTarget.zoom !== undefined) {
      this.setZoom(normalizedTarget.zoom);
    }
    if (normalizedTarget.scale !== undefined) {
      this.setScale(normalizedTarget.scale);
    }
    if (normalizedTarget.rotation !== undefined) {
      this.setRotation(normalizedTarget.rotation);
    }
    if (normalizedTarget.extent !== undefined) {
      this.setExtent(normalizedTarget.extent);
    }
    const payload = hasGoToOptions(options) ? { target, options } : target;
    this.eventBus.emit("view.go-to", payload, this);
    this.emit("go-to", payload);

    return this;
  }

  public async takeScreenshot(options: MapViewTakeScreenshotOptions = {}): Promise<MapViewTakeScreenshotResult> {
    const width = normalizeScreenshotDimension(options.width, 1024);
    const height = normalizeScreenshotDimension(options.height, 768);
    const format = normalizeScreenshotFormat(options.format);
    const mimeType = format === "jpg" ? "image/jpeg" : "image/png";
    const result: MapViewTakeScreenshotResult = {
      data: new Uint8ClampedArray(width * height * 4),
      dataUrl: `data:${mimeType};base64,`,
      width,
      height,
    };

    const payload = {
      width,
      height,
      format,
      quality: options.quality,
      area: options.area,
      ignoreBackground: options.ignoreBackground,
    };
    this.eventBus.emit("view.screenshot", payload, this);
    this.emit("take-screenshot", payload);
    return result;
  }

  public openPopup(options: MapViewPopupOpenOptions = {}): void {
    this.popup.open(options);
  }

  public closePopup(): void {
    this.popup.close();
  }

  public setCenter(center: MapViewCenterLike): void {
    this.center = center;
    this.notifyWatchers("center", this.center);
    this.eventBus.emit("view.center-changed", { center }, this);
  }

  public setZoom(zoom: number | undefined): void {
    this.zoom = zoom;
    this.notifyWatchers("zoom", this.zoom);
    this.eventBus.emit("view.zoom-changed", { zoom }, this);
  }

  public setScale(scale: number | undefined): void {
    this.scale = scale;
    this.notifyWatchers("scale", this.scale);
    this.eventBus.emit("view.scale-changed", { scale }, this);
  }

  public setRotation(rotation: number | undefined): void {
    this.rotation = rotation;
    this.notifyWatchers("rotation", this.rotation);
    this.eventBus.emit("view.rotation-changed", { rotation }, this);
  }

  public setExtent(extent: MapViewExtentLike): void {
    this.extent = extent;
    this.notifyWatchers("extent", this.extent);
    this.eventBus.emit("view.extent-changed", { extent }, this);
  }

  public setPadding(padding: MapViewPaddingLike): void {
    this.padding = padding;
    this.notifyWatchers("padding", this.padding);
    this.eventBus.emit("view.padding-changed", { padding }, this);
  }

  public setConstraints(constraints: MapViewConstraintsLike): void {
    this.constraints = constraints;
    this.notifyWatchers("constraints", this.constraints);
    this.eventBus.emit("view.constraints-changed", { constraints }, this);
  }

  public setHighlightOptions(highlightOptions: MapViewHighlightOptionsLike): void {
    this.highlightOptions = highlightOptions;
    this.notifyWatchers("highlightOptions", this.highlightOptions);
    this.eventBus.emit("view.highlight-options-changed", { highlightOptions }, this);
  }

  public setSpatialReference(spatialReference: MapViewSpatialReferenceLike): void {
    this.spatialReference = spatialReference;
    this.notifyWatchers("spatialReference", this.spatialReference);
    this.eventBus.emit("view.spatial-reference-changed", { spatialReference }, this);
  }

  public async whenLayerView(layer: unknown): Promise<MapViewLayerViewCompat> {
    const existing = this.layerViews.get(layer);
    if (existing) {
      return existing;
    }

    const layerView = new MapViewLayerViewCompat(layer, this.eventBus);
    this.layerViews.set(layer, layerView);
    this.eventBus.emit("view.layer-view-created", { layer, layerView }, this);
    this.eventBus.emit("feature-layer.layerview-create", { view: this, layerView }, this);
    this.emit("layerview-create", { layer, layerView });
    return layerView;
  }

  public on(
    eventName: "click" | "double-click" | "pointer-move" | "pointer-down" | "pointer-up",
    listener: (event: MapViewScreenPoint & { mapPoint?: MapViewMapPoint; [key: string]: unknown }) => void,
  ): MapViewHandle;
  public on(
    eventName: "layerview-create",
    listener: (event: { layer: unknown; layerView: MapViewLayerViewCompat }) => void,
  ): MapViewHandle;
  public on(eventName: string, listener: (event: unknown) => void): MapViewHandle;
  public on(eventName: string, listener: (event: any) => void): MapViewHandle {
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

  public watch(propertyName: "zoom", listener: (value: number | undefined) => void): MapViewHandle;
  public watch(propertyName: "scale", listener: (value: number | undefined) => void): MapViewHandle;
  public watch(propertyName: "rotation", listener: (value: number | undefined) => void): MapViewHandle;
  public watch(propertyName: "center", listener: (value: MapViewCenterLike | undefined) => void): MapViewHandle;
  public watch(propertyName: "extent", listener: (value: MapViewExtentLike | undefined) => void): MapViewHandle;
  public watch(propertyName: "loaded", listener: (value: boolean) => void): MapViewHandle;
  public watch(propertyName: "loadStatus", listener: (value: MapViewLoadStatusCompat) => void): MapViewHandle;
  public watch(
    propertyName: "container",
    listener: (value: HTMLElement | string | null | undefined) => void,
  ): MapViewHandle;
  public watch(propertyName: "padding", listener: (value: MapViewPaddingLike | undefined) => void): MapViewHandle;
  public watch(
    propertyName: "constraints",
    listener: (value: MapViewConstraintsLike | undefined) => void,
  ): MapViewHandle;
  public watch(
    propertyName: "highlightOptions",
    listener: (value: MapViewHighlightOptionsLike | undefined) => void,
  ): MapViewHandle;
  public watch(
    propertyName: "spatialReference",
    listener: (value: MapViewSpatialReferenceLike | undefined) => void,
  ): MapViewHandle;
  public watch(propertyName: "popup.visible", listener: (value: boolean) => void): MapViewHandle;
  public watch(propertyName: "popup.viewModel.active", listener: (value: boolean) => void): MapViewHandle;
  public watch(propertyName: "popup.selectedFeatureIndex", listener: (value: number) => void): MapViewHandle;
  public watch(propertyName: string, listener: (value: unknown) => void): MapViewHandle;
  public watch(propertyName: string, listener: (value: any) => void): MapViewHandle {
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
    this.scale = undefined;
    this.notifyWatchers("scale", this.scale);
    this.rotation = undefined;
    this.notifyWatchers("rotation", this.rotation);
    this.extent = undefined;
    this.notifyWatchers("extent", this.extent);
    this.constraints = undefined;
    this.notifyWatchers("constraints", this.constraints);
    this.padding = undefined;
    this.notifyWatchers("padding", this.padding);
    this.highlightOptions = undefined;
    this.notifyWatchers("highlightOptions", this.highlightOptions);
    this.spatialReference = undefined;
    this.notifyWatchers("spatialReference", this.spatialReference);
    this.loaded = false;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "not-loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventListeners.clear();
    this.watchListeners.clear();
  }

  public emit(eventName: string, payload?: unknown): boolean {
    const listeners = this.eventListeners.get(eventName);
    if (!listeners || listeners.size === 0) {
      return false;
    }

    for (const listener of listeners) {
      safeInvokeListener(() => listener(payload));
    }
    return true;
  }

  protected notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeListener(() => listener(value));
    }
  }
}

interface MapViewExtentBounds {
  xmin: number;
  ymin: number;
  xmax: number;
  ymax: number;
  spatialReference?: MapViewSpatialReferenceLike;
}

const GEOMETRY_SEQUENCE_KEYS = ["points", "path", "paths", "ring", "rings"] as const;

function normalizeGoToTarget(target: MapViewGoToInput): MapViewGoToTarget {
  const normalized: MapViewGoToTarget = {};
  const targetRecord = asRecord(target);

  if (targetRecord !== undefined) {
    if ("target" in targetRecord && targetRecord.target !== undefined) {
      mergeGoToTarget(normalized, normalizeGoToTarget(targetRecord.target as MapViewGoToInput));
    }
    if (targetRecord.center !== undefined) {
      normalized.center = targetRecord.center;
    }
    const zoom = normalizeFiniteNumber(targetRecord.zoom);
    if (zoom !== undefined) {
      normalized.zoom = zoom;
    }
    const scale = normalizeFiniteNumber(targetRecord.scale);
    if (scale !== undefined) {
      normalized.scale = scale;
    }
    const rotation = normalizeFiniteNumber(targetRecord.rotation);
    if (rotation !== undefined) {
      normalized.rotation = rotation;
    }
    if (targetRecord.extent !== undefined) {
      normalized.extent = targetRecord.extent;
    }
  }

  if (normalized.center === undefined) {
    const derivedCenter = extractGoToCenter(target);
    if (derivedCenter !== undefined) {
      normalized.center = derivedCenter;
    }
  }

  if (normalized.extent === undefined) {
    const derivedExtent = extractGoToExtent(target);
    if (derivedExtent !== undefined) {
      normalized.extent = derivedExtent;
    }
  }

  if (normalized.center === undefined && normalized.extent !== undefined) {
    const centerFromExtent = extractExtentCenter(normalized.extent);
    if (centerFromExtent !== undefined) {
      normalized.center = centerFromExtent;
    }
  }

  return normalized;
}

function mergeGoToTarget(target: MapViewGoToTarget, source: MapViewGoToTarget): void {
  if (source.center !== undefined) {
    target.center = source.center;
  }
  if (source.zoom !== undefined) {
    target.zoom = source.zoom;
  }
  if (source.scale !== undefined) {
    target.scale = source.scale;
  }
  if (source.rotation !== undefined) {
    target.rotation = source.rotation;
  }
  if (source.extent !== undefined) {
    target.extent = source.extent;
  }
}

function hasGoToOptions(options: MapViewGoToOptions): boolean {
  return (
    options.animate !== undefined ||
    options.duration !== undefined ||
    options.speedFactor !== undefined ||
    options.easing !== undefined
  );
}

function extractGoToCenter(value: unknown, visited: Set<object> = new Set()): MapViewCenterLike | undefined {
  if (isCoordinatePair(value)) {
    return [value[0], value[1]];
  }

  if (Array.isArray(value)) {
    return undefined;
  }

  if (!isRecord(value)) {
    return undefined;
  }

  if (visited.has(value)) {
    return undefined;
  }
  visited.add(value);

  if ("target" in value && value.target !== undefined) {
    const nestedCenter = extractGoToCenter(value.target, visited);
    if (nestedCenter !== undefined) {
      return nestedCenter;
    }
  }

  if ("geometry" in value && value.geometry !== undefined) {
    const nestedCenter = extractGoToCenter(value.geometry, visited);
    if (nestedCenter !== undefined) {
      return nestedCenter;
    }
  }

  return extractPointCenterFromRecord(value);
}

function extractGoToExtent(value: unknown): MapViewGoToExtentLike | undefined {
  if (!shouldDeriveExtentFromTarget(value)) {
    return undefined;
  }

  const bounds = collectBounds(value);
  return bounds ? toExtentLike(bounds) : undefined;
}

function shouldDeriveExtentFromTarget(value: unknown, visited: Set<object> = new Set()): boolean {
  if (isExtentLike(value)) {
    return true;
  }

  if (isCoordinatePair(value)) {
    return false;
  }

  if (Array.isArray(value)) {
    return value.length > 0;
  }

  if (!isRecord(value)) {
    return false;
  }

  if (visited.has(value)) {
    return false;
  }
  visited.add(value);

  if ("target" in value && value.target !== undefined && shouldDeriveExtentFromTarget(value.target, visited)) {
    return true;
  }

  if ("geometry" in value && value.geometry !== undefined && shouldDeriveExtentFromTarget(value.geometry, visited)) {
    return true;
  }

  return hasCoordinateSequences(value);
}

function collectBounds(value: unknown, visited: Set<object> = new Set()): MapViewExtentBounds | undefined {
  if (isExtentLike(value)) {
    return {
      xmin: value.xmin,
      ymin: value.ymin,
      xmax: value.xmax,
      ymax: value.ymax,
      spatialReference: value.spatialReference,
    };
  }

  if (isCoordinatePair(value)) {
    return createBoundsFromPoint(value[0], value[1]);
  }

  if (Array.isArray(value)) {
    let bounds: MapViewExtentBounds | undefined;
    for (const item of value) {
      bounds = mergeBounds(bounds, collectBounds(item, visited));
    }
    return bounds;
  }

  if (!isRecord(value)) {
    return undefined;
  }

  if (visited.has(value)) {
    return undefined;
  }
  visited.add(value);

  let bounds: MapViewExtentBounds | undefined;

  if ("target" in value && value.target !== undefined) {
    bounds = mergeBounds(bounds, collectBounds(value.target, visited));
  }

  if ("geometry" in value && value.geometry !== undefined) {
    bounds = mergeBounds(bounds, collectBounds(value.geometry, visited));
  }

  const point = extractPointFromRecord(value);
  if (point !== undefined) {
    bounds = mergeBounds(bounds, createBoundsFromPoint(point.x, point.y, point.spatialReference));
  }

  for (const key of GEOMETRY_SEQUENCE_KEYS) {
    if (key in value) {
      bounds = mergeBounds(bounds, collectBounds(value[key], visited));
    }
  }

  return bounds;
}

function hasCoordinateSequences(value: Record<string, any>): boolean {
  return GEOMETRY_SEQUENCE_KEYS.some((key) => key in value && Array.isArray(value[key]));
}

function extractPointCenterFromRecord(value: Record<string, any>): MapViewMapPoint | undefined {
  const point = extractPointFromRecord(value);
  if (point === undefined) {
    return undefined;
  }
  return {
    x: point.x,
    y: point.y,
    spatialReference: point.spatialReference,
  };
}

function extractPointFromRecord(
  value: Record<string, any>,
): { x: number; y: number; spatialReference?: MapViewSpatialReferenceLike } | undefined {
  const x = normalizeFiniteNumber(value.x);
  const y = normalizeFiniteNumber(value.y);
  if (x !== undefined && y !== undefined) {
    return {
      x,
      y,
      spatialReference: value.spatialReference,
    };
  }

  const longitude = normalizeFiniteNumber(value.longitude);
  const latitude = normalizeFiniteNumber(value.latitude);
  if (longitude !== undefined && latitude !== undefined) {
    return {
      x: longitude,
      y: latitude,
      spatialReference: value.spatialReference,
    };
  }

  return undefined;
}

function normalizeFiniteNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function normalizeScreenshotDimension(value: number | undefined, fallback: number): number {
  if (value === undefined || !Number.isFinite(value)) {
    return fallback;
  }
  return Math.max(1, Math.trunc(value));
}

function normalizeScreenshotFormat(format: MapViewTakeScreenshotOptions["format"]): "png" | "jpg" {
  if (format === "jpg" || format === "jpeg") {
    return "jpg";
  }
  return "png";
}

function createBoundsFromPoint(
  x: number,
  y: number,
  spatialReference?: MapViewSpatialReferenceLike,
): MapViewExtentBounds {
  return {
    xmin: x,
    ymin: y,
    xmax: x,
    ymax: y,
    spatialReference,
  };
}

function mergeBounds(
  current: MapViewExtentBounds | undefined,
  next: MapViewExtentBounds | undefined,
): MapViewExtentBounds | undefined {
  if (next === undefined) {
    return current;
  }
  if (current === undefined) {
    return next;
  }

  return {
    xmin: Math.min(current.xmin, next.xmin),
    ymin: Math.min(current.ymin, next.ymin),
    xmax: Math.max(current.xmax, next.xmax),
    ymax: Math.max(current.ymax, next.ymax),
    spatialReference: current.spatialReference ?? next.spatialReference,
  };
}

function toExtentLike(bounds: MapViewExtentBounds): MapViewGoToExtentLike {
  return {
    xmin: bounds.xmin,
    ymin: bounds.ymin,
    xmax: bounds.xmax,
    ymax: bounds.ymax,
    spatialReference: bounds.spatialReference,
  };
}

function extractExtentCenter(extent: unknown): MapViewMapPoint | undefined {
  if (!isExtentLike(extent)) {
    return undefined;
  }
  return {
    x: (extent.xmin + extent.xmax) / 2,
    y: (extent.ymin + extent.ymax) / 2,
    spatialReference: extent.spatialReference,
  };
}

function isCoordinatePair(value: unknown): value is readonly [number, number] {
  return (
    Array.isArray(value) &&
    value.length >= 2 &&
    typeof value[0] === "number" &&
    Number.isFinite(value[0]) &&
    typeof value[1] === "number" &&
    Number.isFinite(value[1])
  );
}

function isExtentLike(value: unknown): value is MapViewGoToExtentLike {
  return (
    isRecord(value) &&
    typeof value.xmin === "number" &&
    Number.isFinite(value.xmin) &&
    typeof value.ymin === "number" &&
    Number.isFinite(value.ymin) &&
    typeof value.xmax === "number" &&
    Number.isFinite(value.xmax) &&
    typeof value.ymax === "number" &&
    Number.isFinite(value.ymax)
  );
}

function normalizeUiOptions(input: MapViewUiPosition | MapViewUiAddOptions): Required<MapViewUiAddOptions> {
  if (typeof input === "string") {
    return {
      position: input,
      index: Number.NaN,
    };
  }

  const position = typeof input.position === "string" && input.position.length > 0 ? input.position : "manual";
  const index = typeof input.index === "number" && Number.isFinite(input.index) ? Math.trunc(input.index) : Number.NaN;
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

function normalizeFeatureIndex(index: number, length: number): number {
  if (!Number.isFinite(index)) {
    return -1;
  }

  const normalized = Math.trunc(index);
  if (normalized < 0 || normalized >= length) {
    return -1;
  }
  return normalized;
}

function normalizeHighlightTargets(
  target: Record<string, unknown> | number | string | readonly (Record<string, unknown> | number | string)[],
): (Record<string, unknown> | number | string)[] {
  if (Array.isArray(target)) {
    return [...target] as (Record<string, unknown> | number | string)[];
  }
  return [target as Record<string, unknown> | number | string];
}

function isQueryFeaturesProvider(value: unknown): value is QueryFeaturesProvider {
  return (
    typeof value === "object" && value !== null && "queryFeatures" in value && typeof value.queryFeatures === "function"
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

function extractGraphicLayer(value: Record<string, unknown>): Record<string, unknown> | undefined {
  if (!("layer" in value) || value.layer === undefined || value.layer === null) {
    return undefined;
  }
  if (typeof value.layer === "object") {
    return value.layer as Record<string, unknown>;
  }
  return undefined;
}

function safeInvokeListener(invoke: () => void): void {
  try {
    invoke();
  } catch {
    // Listener errors should not break compatibility flow.
  }
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}

function asRecord(value: unknown): Record<string, any> | undefined {
  if (!isRecord(value) || Array.isArray(value)) {
    return undefined;
  }
  return value;
}

function extractPopupOptions(popup: Record<string, unknown> | unknown): MapViewPopupCompatOptions {
  if (!isRecord(popup)) {
    return {};
  }

  return {
    autoOpenEnabled: typeof popup.autoOpenEnabled === "boolean" ? popup.autoOpenEnabled : undefined,
    dockEnabled: typeof popup.dockEnabled === "boolean" ? popup.dockEnabled : undefined,
    dockOptions: popup.dockOptions,
  };
}
